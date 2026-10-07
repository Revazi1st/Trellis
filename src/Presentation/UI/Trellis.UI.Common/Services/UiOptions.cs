namespace Trellis.UI.Common.Services;

/// <summary>
/// Switches the host sets for the shared UI. The host decides because only it knows its environment;
/// UI.Common never asks (ADR-0015). Unset options keep their safe default.
/// </summary>
public sealed class UiOptions
{
    /// <summary>
    /// Shows hints that belong on a developer's machine only, such as the seeded admin's well-known
    /// credentials on the sign-in page. Off unless the host turns it on (UI.Web: <c>IsDevelopment()</c>).
    /// </summary>
    public bool ShowDevelopmentHints { get; set; }
}
