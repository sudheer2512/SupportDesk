using System.Text.RegularExpressions;
using SupportDesk.E2ETests.Infrastructure;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace SupportDesk.E2ETests;

public sealed class AgentSignInTests(DeskServerFixture server, ITestOutputHelper output) : E2ETestBase(server, output)
{
    [E2EFact]
    public Task Admin_signs_in_with_mfa_sees_command_center_kpis_and_survives_a_reload() => RunAsync(async () =>
    {
        var page = await NewPageAsync();
        await page.GotoAsync("/login");
        await page.GetByLabel("Workspace").FillAsync(Demo.Tenant);
        await page.GetByLabel("Work e-mail").FillAsync(Demo.Admin);
        await PasswordField(page).FillAsync(Demo.DeskPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();

        // MFA by e-mail code: the demo environment shows the code on screen.
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Two-step verification" })).ToBeVisibleAsync();
        var demoNote = page.GetByRole(AriaRole.Note).Filter(new() { HasText = "Demo environment" });
        var code = (await demoNote.Locator("code").InnerTextAsync()).Trim();
        Assert.Matches(@"^\d{6}$", code);

        // A wrong code is refused and keeps the user on the MFA step.
        await page.GetByLabel("Verification code").FillAsync(code == "000000" ? "111111" : "000000");
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("not valid");

        await page.GetByLabel("Verification code").FillAsync(code);
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify", Exact = true }).ClickAsync();

        // Lands on the Command Center with its KPI tiles loaded.
        await Expect(page).ToHaveURLAsync(new Regex("/command$"));
        await AssertCommandCenterLoadedAsync(page);

        // A full reload keeps the session (refresh cookie → new access token), no trip to /login.
        await page.ReloadAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/command$"));
        await AssertCommandCenterLoadedAsync(page);
    });

    private static async Task AssertCommandCenterLoadedAsync(IPage page)
    {
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Command Center", Level = 1 })).ToBeVisibleAsync();
        var kpis = page.GetByRole(AriaRole.Region, new() { Name = "Key numbers" });
        await Expect(kpis).ToBeVisibleAsync();
        foreach (var label in new[] { "My open tickets", "Unassigned", "SLA at risk", "Escalated" })
        {
            var tile = kpis.GetByRole(AriaRole.Button).Filter(new() { HasText = label }).First;
            await Expect(tile.Locator(".value")).ToHaveTextAsync(new Regex(@"^\s*\d+\s*$"));
        }
    }
}
