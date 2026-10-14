using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SupportDesk.Contracts.Auth;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Tickets;

// SupportDesk HTTP load harness.
// Usage: dotnet run --project tests/SupportDesk.LoadTests -- [baseUrl] [virtualUsers] [seconds] [tenant] [email] [password] [mfaCode]
// Each virtual user runs a realistic agent loop: list a queue page, open a ticket, read its timeline and context,
// and every 5th iteration posts an internal note. Reports throughput, error rate and latency percentiles per operation.

var baseUrl = args.ElementAtOrDefault(0) ?? "http://localhost:5830";
var users = int.TryParse(args.ElementAtOrDefault(1), out var u) ? u : 20;
var seconds = int.TryParse(args.ElementAtOrDefault(2), out var s) ? s : 60;
var tenant = args.ElementAtOrDefault(3) ?? "wings";
var email = args.ElementAtOrDefault(4) ?? "arjun@wings.demo";
var password = args.ElementAtOrDefault(5) ?? "Desk@2026";
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.Add("X-Client", "mobile");
var login = await (await http.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(tenant, email, password), json)).Content.ReadFromJsonAsync<LoginResponse>(json);
if (login?.Status == LoginStatus.MfaRequired)
{
    var code = args.ElementAtOrDefault(6) ?? login.DemoCode ?? throw new InvalidOperationException("MFA required: pass the code as the 7th argument.");
    login = await (await http.PostAsJsonAsync("/api/v1/auth/mfa", new MfaVerifyRequest(login.ChallengeId!.Value, code, "email"), json)).Content.ReadFromJsonAsync<LoginResponse>(json);
}
if (login?.AccessToken is null) { Console.Error.WriteLine($"Login failed: {login?.Message}"); return 1; }
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

var samples = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<double>>();
var errors = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
var deadline = DateTime.UtcNow.AddSeconds(seconds);

async Task<T?> Timed<T>(string op, Func<Task<HttpResponseMessage>> call)
{
    var sw = Stopwatch.StartNew();
    try
    {
        using var r = await call();
        sw.Stop();
        samples.GetOrAdd(op, _ => []).Add(sw.Elapsed.TotalMilliseconds);
        if (!r.IsSuccessStatusCode) { errors.AddOrUpdate(op, 1, (_, n) => n + 1); return default; }
        return typeof(T) == typeof(object) ? default : await r.Content.ReadFromJsonAsync<T>(json);
    }
    catch (Exception)
    {
        errors.AddOrUpdate(op, 1, (_, n) => n + 1);
        return default;
    }
}

Console.WriteLine($"Running {users} virtual users for {seconds}s against {baseUrl} as {email}…");
var total = Stopwatch.StartNew();
await Task.WhenAll(Enumerable.Range(0, users).Select(async vu =>
{
    var rnd = new Random(vu);
    var i = 0;
    while (DateTime.UtcNow < deadline)
    {
        var sort = new[] { "lastActivity", "created", "due", "priority" }[rnd.Next(4)];
        var page = await Timed<PagedResult<TicketListItemDto>>("list", () => http.GetAsync($"/api/v1/tickets?statusCategory=unresolved&sort={sort}&limit=50"));
        if (page is { Items.Count: > 0 })
        {
            var id = page.Items[rnd.Next(page.Items.Count)].Id;
            await Timed<TicketDetailDto>("detail", () => http.GetAsync($"/api/v1/tickets/{id}"));
            await Timed<object>("timeline", () => http.GetAsync($"/api/v1/tickets/{id}/timeline"));
            await Timed<object>("context", () => http.GetAsync($"/api/v1/tickets/{id}/context"));
            if (++i % 5 == 0)
                await Timed<object>("note", () => http.PostAsJsonAsync($"/api/v1/tickets/{id}/notes", new NoteRequest { BodyHtml = $"<p>Load test note {Guid.NewGuid():N}</p>" }, json));
        }
        await Timed<object>("queue-counts", () => http.GetAsync("/api/v1/tickets/queue-counts"));
    }
}));
total.Stop();

static double P(List<double> sorted, double p) => sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Count) - 1, 0, sorted.Count - 1)];
Console.WriteLine();
Console.WriteLine($"{"operation",-14}{"count",8}{"errors",8}{"rps",8}{"p50 ms",9}{"p95 ms",9}{"p99 ms",9}{"max ms",9}");
var all = 0; var allErr = 0;
foreach (var (op, bag) in samples.OrderBy(k => k.Key))
{
    var list = bag.OrderBy(x => x).ToList();
    var err = errors.GetValueOrDefault(op);
    all += list.Count; allErr += err;
    Console.WriteLine($"{op,-14}{list.Count,8}{err,8}{list.Count / total.Elapsed.TotalSeconds,8:0.0}{P(list, 50),9:0}{P(list, 95),9:0}{P(list, 99),9:0}{list.Last(),9:0}");
}
Console.WriteLine($"Total {all} requests in {total.Elapsed.TotalSeconds:0.0}s → {all / total.Elapsed.TotalSeconds:0.0} req/s, error rate {(all == 0 ? 0 : 100.0 * allErr / all):0.00}%");
return allErr * 100 > all ? 2 : 0; // fail if more than 1% errors
