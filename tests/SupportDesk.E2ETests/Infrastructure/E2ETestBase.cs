using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace SupportDesk.E2ETests.Infrastructure;

/// <summary>
/// Base for browser tests: every test gets fresh browser contexts (no shared cookies/storage), screenshots of all open
/// pages plus the API log tail on failure, and sign-in helpers for the desk and the portal.
/// </summary>
[Collection(E2ECollection.Name)]
public abstract class E2ETestBase(DeskServerFixture server, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly List<IBrowserContext> _contexts = [];
    private readonly List<string> _consoleErrors = [];

    protected DeskServerFixture Server { get; } = server;
    protected DeskApi Api => Server.Api;
    protected ITestOutputHelper Output { get; } = output;

    /// <summary>Console errors and failed requests seen by any page of this test (for diagnostics).</summary>
    protected IReadOnlyList<string> ConsoleErrors => _consoleErrors;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _contexts)
        {
            try { await c.CloseAsync(); } catch (PlaywrightException) { }
        }
    }

    /// <summary>A page in a brand-new browser context.</summary>
    protected async Task<IPage> NewPageAsync(BrowserNewContextOptions? options = null)
    {
        var context = await Server.NewContextAsync(options);
        _contexts.Add(context);
        var page = await context.NewPageAsync();
        page.Console += (_, m) =>
        {
            if (m.Type == "error") lock (_consoleErrors) _consoleErrors.Add($"[console] {page.Url}: {m.Text}");
        };
        page.PageError += (_, e) => { lock (_consoleErrors) _consoleErrors.Add($"[pageerror] {page.Url}: {e}"); };
        return page;
    }

    /// <summary>Runs the test body; on failure saves a screenshot of every open page and prints diagnostics.</summary>
    protected async Task RunAsync(Func<Task> body, [CallerMemberName] string test = "")
    {
        try
        {
            await body();
            // The CSP must never block the app itself (scripts, styles, SignalR websockets, data: images, video embeds).
            lock (_consoleErrors)
                Assert.DoesNotContain(_consoleErrors, e => e.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            var i = 0;
            foreach (var page in _contexts.SelectMany(c => c.Pages))
            {
                var file = Path.Combine(E2EEnvironment.ArtifactsDirectory, $"{GetType().Name}.{test}.{++i}.png");
                try
                {
                    await page.ScreenshotAsync(new PageScreenshotOptions { Path = file, FullPage = true, Timeout = 10_000 });
                    Output.WriteLine($"Screenshot: {file} ({page.Url})");
                }
                catch (PlaywrightException) { }
            }
            lock (_consoleErrors)
                foreach (var e in _consoleErrors.TakeLast(20)) Output.WriteLine(e);
            Output.WriteLine("API log tail:" + Environment.NewLine + Server.LogTail(30));
            throw;
        }
    }

    /// <summary>
    /// Signs an agent in through the desk login form, completing e-mail MFA with the code the demo environment shows on
    /// screen. The form pre-fills the remembered workspace/e-mail, so fields are filled (replaced), never typed into.
    /// </summary>
    protected static async Task SignInToDeskAsync(IPage page, string email, string tenant = Demo.Tenant, string password = Demo.DeskPassword)
    {
        await page.GotoAsync("/login");
        await page.GetByLabel("Workspace").FillAsync(tenant);
        await page.GetByLabel("Work e-mail").FillAsync(email);
        await PasswordField(page).FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();

        // Either the app navigates away from /login or the MFA step appears.
        await page.WaitForFunctionAsync(
            "() => !location.pathname.startsWith('/login') || [...document.querySelectorAll('h1')].some(h => h.textContent.includes('Two-step verification'))",
            null, new PageWaitForFunctionOptions { Timeout = 20_000 });
        var mfaHeading = page.GetByRole(AriaRole.Heading, new() { Name = "Two-step verification" });
        if (await mfaHeading.IsVisibleAsync())
        {
            var code = (await page.GetByRole(AriaRole.Note).Locator("code").InnerTextAsync()).Trim();
            Assert.Matches(@"^\d{4,8}$", code);
            await page.GetByLabel("Verification code").FillAsync(code);
            await page.GetByRole(AriaRole.Button, new() { Name = "Verify", Exact = true }).ClickAsync();
        }
        await Expect(page).Not.ToHaveURLAsync(new Regex("/login"), new() { Timeout = 20_000 });
    }

    /// <summary>Signs a customer contact in to a brand's portal with a password.</summary>
    protected static async Task SignInToPortalAsync(IPage page, string email, string brand = Demo.Brand, string password = Demo.PortalPassword)
    {
        await page.GotoAsync($"/portal/{brand}/login");
        await page.GetByLabel("E-mail").FillAsync(email);
        await PasswordField(page).FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Expect(page).Not.ToHaveURLAsync(new Regex("/login"), new() { Timeout = 20_000 });
    }

    /// <summary>
    /// Clicks a control that live notification toasts may cover (realtime "assigned to you" / SLA toasts stack up over the
    /// bottom of a phone screen): dismisses the visible toasts first and retries if new ones arrive meanwhile.
    /// </summary>
    protected static async Task ClickPastToastsAsync(IPage page, ILocator target)
    {
        for (var attempt = 1; ; attempt++)
        {
            var dismiss = page.GetByRole(AriaRole.Button, new() { Name = "Dismiss notification" });
            for (var i = 0; i < 10 && await dismiss.CountAsync() > 0; i++)
            {
                try { await dismiss.First.ClickAsync(new() { Timeout = 2_000 }); } catch (TimeoutException) { }
            }
            try
            {
                await target.ClickAsync(new() { Timeout = 4_000 });
                return;
            }
            catch (TimeoutException) when (attempt < 4) { }
        }
    }

    /// <summary>
    /// The password input. By role: a label match would also hit the portal's "Password" sign-in tab panel and the desk's
    /// "Show password" toggle.
    /// </summary>
    protected static ILocator PasswordField(IPage page) => page.GetByRole(AriaRole.Textbox, new() { Name = "Password" });

    /// <summary>A short unique marker for text the test creates (so assertions never match seed data).</summary>
    protected static string Marker(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";
}
