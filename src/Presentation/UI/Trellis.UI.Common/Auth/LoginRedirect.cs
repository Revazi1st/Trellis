namespace Trellis.UI.Common.Auth;

/// <summary>
/// The round trip through the sign-in page: a protected page sends an anonymous user to
/// <c>login?returnUrl=…</c> (<see cref="From"/>), and the sign-in page sends them back
/// (<see cref="LocalReturnUrl"/>) — only ever to a page of this app.
/// </summary>
public static class LoginRedirect
{
    /// <summary>The query parameter that carries the page to return to.</summary>
    public const string ReturnUrlParameter = "returnUrl";

    /// <summary>Longer than any page address this app has; anything longer is not one.</summary>
    private const int MaxLength = 2048;

    /// <summary>
    /// The sign-in address for a user sent away from <paramref name="relativePath"/> (the page's address
    /// relative to the app's base, as <c>NavigationManager.ToBaseRelativePath</c> gives it).
    /// </summary>
    public static string From(string? relativePath)
        => LocalReturnUrl(relativePath) is { } local
            ? $"login?{ReturnUrlParameter}={Uri.EscapeDataString(local)}"
            : "login";

    /// <summary>
    /// <paramref name="candidate"/> when it is a plain path inside the app, relative to its base
    /// (<c>invoices/42</c>, <c>invoices?page=2</c>); otherwise null. The query string is anyone's to write,
    /// so everything that could leave the app or climb out of its base is refused: an absolute URL or any
    /// scheme (<c>https:</c>, <c>javascript:</c>), a rooted or protocol-relative path (<c>/x</c>,
    /// <c>//evil.example</c>), a backslash (browsers read it as a slash), whitespace or a control character
    /// (browsers strip some of them), and a dot segment (<c>..</c>, also percent-encoded). No open redirect.
    /// </summary>
    public static string? LocalReturnUrl(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)
            || candidate.Length > MaxLength
            || candidate[0] == '/'
            || candidate.Any(static c => c == '\\' || char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return null;
        }

        int pathEnd = candidate.IndexOfAny(['?', '#']);
        string path = pathEnd < 0 ? candidate : candidate[..pathEnd];
        int firstSlash = path.IndexOf('/', StringComparison.Ordinal);
        string firstSegment = firstSlash < 0 ? path : path[..firstSlash];
        if (firstSegment.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        if (Uri.UnescapeDataString(path).Split('/').Any(static segment => segment is "." or ".."))
        {
            return null;
        }

        return Uri.TryCreate(candidate, UriKind.Relative, out _) ? candidate : null;
    }
}
