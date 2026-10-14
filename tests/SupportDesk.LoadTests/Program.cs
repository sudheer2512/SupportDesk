using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SupportDesk.Contracts.Auth;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Tickets;

// SupportDesk HTTP load harness.
// Usage: dotnet run -c Release --project tests/SupportDesk.LoadTests -- [baseUrl] [virtualUsers] [seconds] [tenant] [email] [password] [mfaCode]
//        [--seed-tickets N] [--markdown results.md]
// Each virtual user runs a realistic agent loop against the seeded demo tenant:
//   list all tickets (keyset, random sort) and follow the cursor to page 2, open a queue page, open a ticket (detail,
//   timeline, context), search (global search + ticket list free text), run an analytics query, and every 5th iteration
//   post a public reply (forced, so collisions between virtual users are not counted as errors) or an internal note.
// --seed-tickets N first creates N tickets through the API (data volume for index/plan analysis).
// A short warm-up (not measured) runs first so JIT/query compilation and cold caches do not skew the percentiles.
// Reports throughput, error count and latency percentiles per operation; exits 2 when more than 1% of requests failed.

string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var positional = args.Where((a, i) => !a.StartsWith("--") && (i == 0 || !args[i - 1].StartsWith("--"))).ToList();
var markdownPath = Option("--markdown");
var baseUrl = positional.ElementAtOrDefault(0) ?? "http://localhost:5830";
var users = int.TryParse(positional.ElementAtOrDefault(1), out var u) ? u : 20;
var seconds = int.TryParse(positional.ElementAtOrDefault(2), out var s) ? s : 60;
var tenant = positional.ElementAtOrDefault(3) ?? "wings";
var email = positional.ElementAtOrDefault(4) ?? "priya@wings.demo";
var password = positional.ElementAtOrDefault(5) ?? "Desk@2026";
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

var handler = new SocketsHttpHandler { MaxConnectionsPerServer = Math.Max(16, users * 2), PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
using var http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.Add("X-Client", "mobile");
var login = await (await http.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(tenant, email, password), json)).Content.ReadFromJsonAsync<LoginResponse>(json);
if (login?.Status == LoginStatus.MfaRequired)
{
    var code = positional.ElementAtOrDefault(6) ?? login.DemoCode ?? throw new InvalidOperationException("MFA required: pass the code as the 7th argument.");
    login = await (await http.PostAsJsonAsync("/api/v1/auth/mfa", new MfaVerifyRequest(login.ChallengeId!.Value, code, "email"), json)).Content.ReadFromJsonAsync<LoginResponse>(json);
}
if (login?.AccessToken is null) { Console.Error.WriteLine($"Login failed: {login?.Message}"); return 1; }
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

var samples = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<double>>();
var errors = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
var errorDetail = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
var measuring = false;

async Task<T?> Timed<T>(string op, Func<Task<HttpResponseMessage>> call)
{
    var sw = Stopwatch.StartNew();
    try
    {
        using var r = await call();
        var body = r.IsSuccessStatusCode && typeof(T) != typeof(object) ? await r.Content.ReadFromJsonAsync<T>(json) : default;
        if (!r.IsSuccessStatusCode) await r.Content.ReadAsByteArrayAsync();
        sw.Stop();
        if (!measuring) return body;
        samples.GetOrAdd(op, _ => []).Add(sw.Elapsed.TotalMilliseconds);
        if (!r.IsSuccessStatusCode)
        {
            errors.AddOrUpdate(op, 1, (_, n) => n + 1);
            errorDetail.AddOrUpdate($"{op} {(int)r.StatusCode}", 1, (_, n) => n + 1);
        }
        return body;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
        if (measuring)
        {
            samples.GetOrAdd(op, _ => []).Add(sw.Elapsed.TotalMilliseconds);
            errors.AddOrUpdate(op, 1, (_, n) => n + 1);
            errorDetail.AddOrUpdate($"{op} {ex.GetType().Name}", 1, (_, n) => n + 1);
        }
        return default;
    }
}

string[] sorts = ["lastActivity", "created", "due", "priority"];
string[] terms = ["invoice", "GST", "printer", "payroll", "sync", "export", "licence", "password", "e-way", "report"];
string[] metrics = ["ticket_count", "avg_first_response_hours", "median_resolution_hours", "sla_breaches", "backlog", "csat_avg"];
string[] dimensions = ["department", "priority", "channel", "assignee", "day"];

// Optional data volume: --seed-tickets N creates N tickets through the API first (realistic rows for index/plan analysis).
if (int.TryParse(Option("--seed-tickets"), out var seedTickets) && seedTickets > 0)
{
    string[] subjects = ["Invoice total does not match", "GST rounding on e-invoice", "Printer offline at billing counter", "Payroll PF computed on gross",
        "POS sync queue stuck", "Report export times out", "Licence renewal reminder", "Password reset link expired", "E-way bill error 2172",
        "Stock valuation differs between reports", "Bank reconciliation duplicates", "Dashboard shows stale data"];
    string[] channels = ["Email", "Phone", "Portal", "Chat", "WhatsApp"];
    string[] priorities = ["Low", "Medium", "High", "Urgent"];
    var created = 0;
    var seedWatch = Stopwatch.StartNew();
    await Parallel.ForEachAsync(Enumerable.Range(0, seedTickets), new ParallelOptions { MaxDegreeOfParallelism = 12 }, async (n, ct) =>
    {
        var rnd = new Random(n);
        var subject = $"{subjects[rnd.Next(subjects.Length)]} ({terms[rnd.Next(terms.Length)]} #{n})";
        var r = await http.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest
        {
            Subject = subject, DescriptionHtml = $"<p>{subject}. Generated by the load harness for volume testing.</p>",
            Channel = channels[rnd.Next(channels.Length)], Priority = priorities[rnd.Next(priorities.Length)], ClientRequestId = $"load-seed-{n}"
        }, json, ct);
        if (r.IsSuccessStatusCode && Interlocked.Increment(ref created) % 1000 == 0)
            Console.WriteLine($"  seeded {created} tickets ({created / seedWatch.Elapsed.TotalSeconds:0}/s)");
    });
    Console.WriteLine($"Seeded {created}/{seedTickets} tickets in {seedWatch.Elapsed.TotalSeconds:0}s.");
}

async Task AgentLoop(int vu, DateTime until)
{
    var rnd = new Random(vu);
    var i = 0;
    while (DateTime.UtcNow < until)
    {
        var sort = sorts[rnd.Next(sorts.Length)];
        // Keyset pagination over every visible ticket: first page plus the next one through its cursor.
        var everything = await Timed<PagedResult<TicketListItemDto>>("list", () => http.GetAsync($"/api/v1/tickets?sort={sort}&limit=25"));
        if (everything?.NextCursor is { } cursor)
            await Timed<PagedResult<TicketListItemDto>>("list-next-page", () => http.GetAsync($"/api/v1/tickets?sort={sort}&limit=25&cursor={Uri.EscapeDataString(cursor)}"));
        var page = await Timed<PagedResult<TicketListItemDto>>("queue", () => http.GetAsync($"/api/v1/tickets?statusCategory=unresolved&sort={sort}&limit=50"));
        if (page is { Items.Count: > 0 })
        {
            var id = page.Items[rnd.Next(page.Items.Count)].Id;
            await Timed<TicketDetailDto>("detail", () => http.GetAsync($"/api/v1/tickets/{id}"));
            await Timed<object>("timeline", () => http.GetAsync($"/api/v1/tickets/{id}/timeline"));
            await Timed<object>("context", () => http.GetAsync($"/api/v1/tickets/{id}/context"));
            if (++i % 5 == 0)
            {
                if (rnd.Next(2) == 0)
                    await Timed<object>("reply", () => http.PostAsJsonAsync($"/api/v1/tickets/{id}/replies",
                        new ReplyRequest { BodyHtml = $"<p>Load test reply {Guid.NewGuid():N}</p>", Force = true, ClientRequestId = Guid.NewGuid().ToString("N") }, json));
                else
                    await Timed<object>("note", () => http.PostAsJsonAsync($"/api/v1/tickets/{id}/notes",
                        new NoteRequest { BodyHtml = $"<p>Load test note {Guid.NewGuid():N}</p>", ClientRequestId = Guid.NewGuid().ToString("N") }, json));
            }
        }
        var term = terms[rnd.Next(terms.Length)];
        await Timed<object>("search", () => http.GetAsync($"/api/v1/search?q={Uri.EscapeDataString(term)}&limit=20"));
        await Timed<object>("list-search", () => http.GetAsync($"/api/v1/tickets?q={Uri.EscapeDataString(term)}&limit=25"));
        await Timed<object>("analytics", () => http.GetAsync(
            $"/api/v1/analytics/query?metric={metrics[rnd.Next(metrics.Length)]}&dimension={dimensions[rnd.Next(dimensions.Length)]}&period=last_30_days"));
        await Timed<object>("queue-counts", () => http.GetAsync("/api/v1/tickets/queue-counts"));
    }
}

var warmup = Math.Clamp(seconds / 6, 5, 15);
Console.WriteLine($"Warming up for {warmup}s, then running {users} virtual users for {seconds}s against {baseUrl} as {email}…");
var warmDeadline = DateTime.UtcNow.AddSeconds(warmup);
await Task.WhenAll(Enumerable.Range(0, Math.Min(users, 4)).Select(vu => AgentLoop(1000 + vu, warmDeadline)));

measuring = true;
var deadline = DateTime.UtcNow.AddSeconds(seconds);
var total = Stopwatch.StartNew();
await Task.WhenAll(Enumerable.Range(0, users).Select(vu => AgentLoop(vu, deadline)));
total.Stop();

static double P(List<double> sorted, double p) => sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Count) - 1, 0, sorted.Count - 1)];
var md = new StringBuilder();
md.AppendLine("| operation | count | errors | req/s | p50 ms | p95 ms | p99 ms | max ms |");
md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
Console.WriteLine();
Console.WriteLine($"{"operation",-16}{"count",8}{"errors",8}{"rps",8}{"p50 ms",9}{"p95 ms",9}{"p99 ms",9}{"max ms",9}");
var all = 0; var allErr = 0;
foreach (var (op, bag) in samples.OrderBy(k => k.Key))
{
    var list = bag.OrderBy(x => x).ToList();
    var err = errors.GetValueOrDefault(op);
    all += list.Count; allErr += err;
    var rps = list.Count / total.Elapsed.TotalSeconds;
    Console.WriteLine($"{op,-16}{list.Count,8}{err,8}{rps,8:0.0}{P(list, 50),9:0}{P(list, 95),9:0}{P(list, 99),9:0}{list.Last(),9:0}");
    md.AppendLine($"| {op} | {list.Count} | {err} | {rps:0.0} | {P(list, 50):0} | {P(list, 95):0} | {P(list, 99):0} | {list.Last():0} |");
}
var summary = $"Total {all} requests in {total.Elapsed.TotalSeconds:0.0}s → {all / total.Elapsed.TotalSeconds:0.0} req/s, error rate {(all == 0 ? 0 : 100.0 * allErr / all):0.00}%";
Console.WriteLine(summary);
foreach (var (key, n) in errorDetail.OrderBy(k => k.Key)) Console.WriteLine($"  error: {key} × {n}");
if (markdownPath is not null)
{
    md.AppendLine().AppendLine(summary);
    await File.WriteAllTextAsync(markdownPath, md.ToString());
}
return allErr * 100 > all ? 2 : 0; // fail if more than 1% errors
