using Microsoft.Playwright;

namespace Trellis.UiTests;

/// <summary>
/// Signing in as an arrange step, through the sign-in page as a user does. The UI keeps its tokens in the
/// circuit (ADR-0007), so a session lasts as long as the page: a full page load (GotoAsync) starts signed out,
/// and a signed-in test moves on through the app's own links. The API allows ten sign-ins a minute from one
/// address, and every browser test signs in from the web host's, so a test that visits many protected pages
/// signs in once (<see cref="DesignSystemProofTests"/> shares one signed-in page).
/// </summary>
internal static class Session
{
    /// <summary>Signs in as the seeded admin and waits for the invoice list the sign-in lands on.</summary>
    public static async Task SignInAsAdminAsync(IPage page)
    {
        await page.GotoAsync($"{UiTestEnvironment.WebBaseUrl}/login");
        // Wait for the Blazor Server circuit to attach — values typed before it are lost.
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.FillAsync("input[autocomplete='username']", UiTestEnvironment.SeededAdminEmail);
        await page.FillAsync("input[autocomplete='current-password']", UiTestEnvironment.SeededAdminPassword);
        await page.ClickAsync("button[type='submit']");
        await page.WaitForURLAsync("**/invoices", new() { Timeout = 15_000 });
    }
}
