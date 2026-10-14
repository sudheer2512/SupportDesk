using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tenancy;
using SupportDesk.Contracts.Auth;
using SupportDesk.Domain;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;
using SupportDesk.Workers;

namespace SupportDesk.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API against a throwaway LocalDB database (schema from the model). Background workers are disabled;
/// tests drive the outbox, jobs and recurring tasks explicitly for deterministic results. Each test creates its own
/// tenant, so tests share one database without interfering (which itself exercises tenant isolation).
/// </summary>
public sealed class DeskApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Password = "Test@Pass2026";
    private readonly string _database = $"SupportDesk_Test_{Guid.NewGuid():N}"[..26];
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "supportdesk-tests", Guid.NewGuid().ToString("N"));

    public string ConnectionString => $"Server=(localdb)\\MSSQLLocalDB;Database={_database};Trusted_Connection=True;TrustServerCertificate=True";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:SupportDesk", ConnectionString);
        builder.UseSetting("Database:InitializeMode", "EnsureCreated");
        builder.UseSetting("Seed:Demo", "false");
        builder.UseSetting("Workers:Enabled", "false");
        builder.UseSetting("Storage:DataPath", _dataPath);
        builder.UseSetting("Security:MasterKey", Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));
        builder.UseSetting("RateLimits:AuthPerMinute", "100000");
        builder.UseSetting("RateLimits:ApiPerMinute", "1000000");
        builder.UseSetting("RateLimits:PublicPerMinute", "100000");
        builder.UseSetting("Automation:Webhooks:AllowPrivateNetworks", "true");
        builder.UseSetting("ForwardedHeaders:TrustAll", "true"); // tests simulate client IPs with X-Forwarded-For
        builder.ConfigureTestServices(s => s.AddScoped<IJobHandler, TestCounterJobHandler>());
    }

    public Task InitializeAsync()
    {
        _ = Server; // builds the host: creates the schema and platform catalogs
        return Task.CompletedTask;
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection("Server=(localdb)\\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=True");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"IF DB_ID('{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
        await cmd.ExecuteNonQueryAsync();
        try { Directory.Delete(_dataPath, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public MutableClock Clock => Services.GetRequiredService<MutableClock>();

    /// <summary>Provisions a fresh tenant (Enterprise plan, two departments) and returns handles for it.</summary>
    public async Task<TestTenant> CreateTenantAsync(string? slug = null, string plan = "enterprise")
    {
        slug ??= "t-" + Guid.NewGuid().ToString("N")[..10];
        using var scope = Services.CreateScope();
        var provisioned = await scope.ServiceProvider.GetRequiredService<TenantProvisioningService>().ProvisionAsync(
            new ProvisionTenantRequest(slug, $"Tenant {slug}", plan, $"admin@{slug}.test", "Admin", Password, "Asia/Kolkata", "IN", Trial: false,
                Departments: ["Support", "Billing"]));
        return new TestTenant(this, provisioned.TenantId, slug, provisioned);
    }

    /// <summary>Runs code in a scope with a system request context for the tenant.</summary>
    public async Task<T> InTenantAsync<T>(Guid tenantId, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<RequestContext>().SetSystem(tenantId, "Test");
        return await work(scope.ServiceProvider);
    }

    public Task InTenantAsync(Guid tenantId, Func<IServiceProvider, Task> work) => InTenantAsync(tenantId, async sp => { await work(sp); return 0; });

    /// <summary>Dispatches committed outbox events until none are due.</summary>
    public async Task DrainOutboxAsync()
    {
        var processor = Services.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 50 && await processor.ProcessBatchAsync(100, default) > 0; i++) { }
    }

    public Task<int> DrainJobsAsync() => Services.GetRequiredService<JobProcessor>().DrainAsync(default);

    public Task RunRecurringAsync(string name) => Services.GetRequiredService<RecurringTaskProcessor>().RunNowAsync(name, default);

    public HttpClient ClientWithToken(string token)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<string> LoginAsync(string tenant, string email, string password = Password)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Client", "mobile");
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(tenant, email, password), Json);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>(Json);
        if (body?.AccessToken is null) throw new InvalidOperationException($"Login failed for {email}: {response.StatusCode} {body?.Message}");
        return body.AccessToken;
    }
}

/// <summary>A provisioned tenant with helpers to create agents and authenticated clients.</summary>
public sealed class TestTenant(DeskApiFactory factory, Guid id, string slug, ProvisionedTenant provisioned)
{
    public Guid Id { get; } = id;
    public string Slug { get; } = slug;
    public ProvisionedTenant Provisioned { get; } = provisioned;
    public string AdminEmail => $"admin@{Slug}.test";
    public Guid SupportDepartmentId => Provisioned.Departments["Support"];
    public Guid BillingDepartmentId => Provisioned.Departments["Billing"];

    public async Task<HttpClient> AdminClientAsync() => factory.ClientWithToken(await factory.LoginAsync(Slug, AdminEmail));

    /// <summary>Creates an agent with the given role in the given departments (team membership in each department's default team).</summary>
    public async Task<(Guid UserId, string Email)> CreateAgentAsync(string name, string role = "Agent", params Guid[] departments)
    {
        var email = $"{name.ToLowerInvariant().Replace(' ', '.')}.{Guid.NewGuid().ToString("N")[..4]}@{Slug}.test";
        var userId = await factory.InTenantAsync(Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var user = new User
            {
                Email = email, EmailNormalized = email, DisplayName = name, Kind = role == "Light Agent" ? UserKind.LightAgent : UserKind.Agent,
                PasswordHash = sp.GetRequiredService<PasswordService>().Hash(DeskApiFactory.Password)
            };
            db.Users.Add(user);
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = Provisioned.Roles[role] });
            db.Agents.Add(new Agent { UserId = user.Id, Availability = AgentAvailability.Online, IsLightAgent = role == "Light Agent" });
            foreach (var d in departments.Length == 0 ? [SupportDepartmentId] : departments)
            {
                db.AgentDepartments.Add(new AgentDepartment { UserId = user.Id, DepartmentId = d, IsSupervisor = role == "Supervisor" });
                var team = await db.Teams.FirstAsync(t => t.DepartmentId == d);
                db.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = user.Id });
            }
            await db.SaveChangesAsync();
            return user.Id;
        });
        return (userId, email);
    }

    public async Task<HttpClient> AgentClientAsync(string email) => factory.ClientWithToken(await factory.LoginAsync(Slug, email));
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<DeskApiFactory>;

/// <summary>Counts executions per payload key (restart-recovery and idempotency tests).</summary>
public sealed class TestCounterJobHandler : IJobHandler
{
    public const string JobType = "test.counter";
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Executions = new();
    public string Type => JobType;

    public Task ExecuteAsync(Domain.Platform.BackgroundJob job, CancellationToken ct)
    {
        Executions.AddOrUpdate(job.PayloadJson, 1, (_, n) => n + 1);
        return Task.CompletedTask;
    }
}
