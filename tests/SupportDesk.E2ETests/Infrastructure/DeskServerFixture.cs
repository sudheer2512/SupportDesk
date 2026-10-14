using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace SupportDesk.E2ETests.Infrastructure;

/// <summary>
/// Starts the real SupportDesk API as a separate process for the whole suite: builds the Angular clients
/// (<c>npm run build</c> → the API's wwwroot), runs the API's build output on a free port of 127.0.0.21 against a throwaway
/// LocalDB database with the demo seed, waits until the demo login works, and launches the installed Chrome through
/// Playwright. Teardown kills the API by its PID and drops the database.
/// </summary>
public sealed class DeskServerFixture : IAsyncLifetime
{
    public const string Host = "127.0.0.21";
    public const string MasterKey = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";

    private readonly string _database = $"SupportDesk_E2E_{Guid.NewGuid():N}"[..28];
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "supportdesk-e2e", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _logTail = new();
    private Process? _api;
    private StreamWriter? _log;
    private IPlaywright? _playwright;

    public string BaseUrl { get; private set; } = "";
    public IBrowser Browser { get; private set; } = null!;
    public DeskApi Api { get; private set; } = null!;
    public string LogFile { get; } = Path.Combine(E2EEnvironment.ArtifactsDirectory, $"api-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    private static string MasterConnectionString =>
        Environment.GetEnvironmentVariable("SUPPORTDESK_E2E_CONNECTION")
        ?? "Server=(localdb)\\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private string DatabaseConnectionString => new SqlConnectionStringBuilder(MasterConnectionString) { InitialCatalog = _database }.ConnectionString;

    public async Task InitializeAsync()
    {
        if (E2EEnvironment.SkipReason is not null) return;
        var total = Stopwatch.StartNew();
        if (!E2EEnvironment.SkipClientBuild) await BuildClientsAsync();
        StartApi();
        await WaitUntilReadyAsync(TimeSpan.FromMinutes(6));
        Log($"[e2e] API ready at {BaseUrl} after {total.Elapsed.TotalSeconds:0}s (database {_database}).");

        _playwright = await Playwright.CreateAsync();
        var chrome = Environment.GetEnvironmentVariable("SUPPORTDESK_E2E_CHROME");
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = string.IsNullOrEmpty(chrome) ? "chrome" : null,
            ExecutablePath = string.IsNullOrEmpty(chrome) ? null : chrome,
            Headless = !E2EEnvironment.Headed
        });
        Assertions.SetDefaultExpectTimeout(15_000);
    }

    public async Task DisposeAsync()
    {
        if (E2EEnvironment.SkipReason is not null) return;
        try
        {
            if (Browser is not null) await Browser.CloseAsync();
        }
        catch (PlaywrightException) { }
        _playwright?.Dispose();
        Api?.Dispose();
        StopApi();
        await DropDatabaseAsync();
        try { Directory.Delete(_dataPath, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _log?.Dispose();
    }

    /// <summary>A fresh, isolated browser context (own cookies and storage) on the desk/portal origin.</summary>
    public async Task<IBrowserContext> NewContextAsync(BrowserNewContextOptions? options = null)
    {
        options ??= new BrowserNewContextOptions();
        options.BaseURL = BaseUrl;
        options.ViewportSize ??= new ViewportSize { Width = 1440, Height = 900 };
        options.Locale ??= "en-US";
        var context = await Browser.NewContextAsync(options);
        context.SetDefaultTimeout(20_000);
        return context;
    }

    /// <summary>The last lines the API wrote (attached to failures).</summary>
    public string LogTail(int lines = 40) =>
        string.Join(Environment.NewLine, _logTail.Where(l => !l.Contains("skipped stale event") && !l.StartsWith("info: SupportDesk.Application.Automation.AutomationOutboxHandler")).TakeLast(lines));

    // ───── API process ─────

    private static async Task BuildClientsAsync()
    {
        var clients = Path.Combine(E2EEnvironment.RepoRoot, "clients");
        if (!Directory.Exists(Path.Combine(clients, "node_modules")))
            throw new InvalidOperationException($"clients/node_modules is missing: run 'npm ci' in {clients} first (or set SUPPORTDESK_E2E_SKIP_CLIENT_BUILD=1 with a built wwwroot).");
        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var psi = new ProcessStartInfo(windows ? "cmd.exe" : "npm", windows ? "/d /s /c npm run build" : "run build")
        {
            WorkingDirectory = clients,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.Environment["NG_CLI_ANALYTICS"] = "false";
        psi.Environment["FORCE_COLOR"] = "0";
        var output = new StringBuilder();
        using var npm = new Process { StartInfo = psi };
        npm.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        npm.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        npm.Start();
        npm.BeginOutputReadLine();
        npm.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        try
        {
            await npm.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            npm.Kill(entireProcessTree: true);
            throw new TimeoutException("npm run build did not finish within 12 minutes.");
        }
        if (npm.ExitCode != 0)
        {
            string text;
            lock (output) text = output.ToString();
            throw new InvalidOperationException($"npm run build failed (exit {npm.ExitCode}):{Environment.NewLine}{text[Math.Max(0, text.Length - 4000)..]}");
        }
    }

    private void StartApi()
    {
        var apiDir = Path.Combine(E2EEnvironment.RepoRoot, "src", "SupportDesk.Api");
        var dll = Path.Combine(apiDir, "bin", E2EEnvironment.Configuration, "net10.0", "SupportDesk.Api.dll");
        if (!File.Exists(dll)) throw new InvalidOperationException($"API build output not found: {dll}. Build the solution first.");
        if (!File.Exists(Path.Combine(apiDir, "wwwroot", "index.html")) || !File.Exists(Path.Combine(apiDir, "wwwroot", "portal", "index.html")))
            throw new InvalidOperationException("The desk/portal SPAs are not built into src/SupportDesk.Api/wwwroot (cd clients && npm run build).");

        var port = FreePort();
        BaseUrl = $"http://{Host}:{port}";
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = apiDir, // content root → wwwroot and appsettings.Development.json
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add(BaseUrl);
        var env = psi.Environment;
        env["ASPNETCORE_ENVIRONMENT"] = "Development";
        env.Remove("ASPNETCORE_URLS");
        env["ConnectionStrings__SupportDesk"] = DatabaseConnectionString;
        env["Database__InitializeMode"] = "EnsureCreated";
        env["Seed__Demo"] = "true";
        env["Security__MasterKey"] = MasterKey;
        env["Storage__DataPath"] = _dataPath;
        env["RateLimits__AuthPerMinute"] = "100000";
        env["RateLimits__ApiPerMinute"] = "1000000";
        env["RateLimits__PublicPerMinute"] = "100000";
        env["Logging__Console__FormatterName"] = "simple";

        _log = new StreamWriter(LogFile, append: false) { AutoFlush = true };
        _api = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _api.OutputDataReceived += (_, e) => Log(e.Data);
        _api.ErrorDataReceived += (_, e) => Log(e.Data);
        if (!_api.Start()) throw new InvalidOperationException("Could not start the API process.");
        _api.BeginOutputReadLine();
        _api.BeginErrorReadLine();
        Log($"[e2e] Started API pid {_api.Id}: {dll} {BaseUrl} (database {_database})");
    }

    private async Task WaitUntilReadyAsync(TimeSpan timeout)
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTime.UtcNow + timeout;
        // 1. The host answers /health/ready once migrations/EnsureCreated and the demo seed have finished.
        while (true)
        {
            if (_api!.HasExited) throw new InvalidOperationException($"The API exited during start-up (code {_api.ExitCode}).{Environment.NewLine}{LogTail()}");
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"The API was not ready within {timeout}.{Environment.NewLine}{LogTail()}");
            try
            {
                using var r = await http.GetAsync("/health/ready");
                if (r.StatusCode == HttpStatusCode.OK) break;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(1000);
        }
        // 2. Seeding is complete when the demo agent login works.
        Api = new DeskApi(BaseUrl);
        while (true)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"The demo login never succeeded.{Environment.NewLine}{LogTail()}");
            try
            {
                using var r = await http.PostAsJsonAsync("/api/v1/auth/login", new { tenant = "wings", email = Demo.Admin, password = Demo.DeskPassword });
                var body = await r.Content.ReadFromJsonAsync<JsonObject>();
                if (r.IsSuccessStatusCode && body?["status"]?.GetValue<string>() is "ok" or "mfa_required") return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(1000);
        }
    }

    private void StopApi()
    {
        if (_api is null) return;
        try
        {
            if (!_api.HasExited)
            {
                Log($"[e2e] Stopping API pid {_api.Id}");
                _api.Kill(entireProcessTree: true); // by PID — never by image name (other SupportDesk.Api processes may be running)
                _api.WaitForExit(30_000);
            }
        }
        catch (InvalidOperationException) { }
        _api.Dispose();
    }

    private async Task DropDatabaseAsync()
    {
        SqlConnection.ClearAllPools();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await using var conn = new SqlConnection(MasterConnectionString);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"IF DB_ID('{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync();
                Log($"[e2e] Dropped database {_database}");
                return;
            }
            catch (SqlException ex) when (attempt < 5)
            {
                Log($"[e2e] Drop database attempt {attempt} failed: {ex.Message}");
                await Task.Delay(2000);
            }
        }
    }

    private void Log(string? line)
    {
        if (line is null) return;
        _logTail.Enqueue(line);
        while (_logTail.Count > 400) _logTail.TryDequeue(out _);
        try { _log?.WriteLine(line); } catch (ObjectDisposedException) { }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Parse(Host), 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

[CollectionDefinition(Name)]
public sealed class E2ECollection : ICollectionFixture<DeskServerFixture>
{
    public const string Name = "e2e";
}

/// <summary>Demo seed identities (docs/DEVELOPMENT.md).</summary>
public static class Demo
{
    public const string Tenant = "wings";
    public const string Brand = "wings";
    public const string DeskPassword = "Desk@2026";
    public const string PortalPassword = "Portal@2026";
    public const string Admin = "admin@wings.demo";
    public const string Supervisor = "priya@wings.demo";
    public const string Agent = "arjun@wings.demo";
    public const string BillingAgent = "sana@wings.demo";
    public const string Customer = "ramesh@abcretail.demo";
    public const string OtherTenant = "northwind";
    public const string OtherTenantAdmin = "admin@northwind.demo";
}
