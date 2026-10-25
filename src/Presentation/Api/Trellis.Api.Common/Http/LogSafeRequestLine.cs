using Microsoft.AspNetCore.Http;

namespace Trellis.Api.Common.Http;

/// <summary>
/// The request line as a log argument. The lines about a request the host could not answer normally (the
/// malformed-request 400, the unhandled-exception 500, the idempotency residue) name its method and path,
/// and both are the client's: a line break in either would end the log line and start a forged one
/// (CWE-117). On Kestrel neither ever carries one — a method is admitted only as RFC 9110 token characters,
/// so a CR or LF never reaches the application, and <see cref="PathString.ToString"/> renders the
/// URI-escaped form, so a line break in the decoded path is logged as <c>%0A</c> — and both methods here
/// return the text unchanged. They still pass it through <see cref="string.ReplaceLineEndings(string)"/>:
/// that covers a less strict server and a test that sets the values by hand, and it is the sanitiser CodeQL
/// recognises (<c>cs/log-forging</c>), so a project generated from Trellis, which inherits the CodeQL
/// workflow, starts without those alerts. The message templates keep their <c>{Method}</c> and
/// <c>{Path}</c> properties; only the arguments go through here.
/// </summary>
internal static class LogSafeRequestLine
{
    /// <summary>The request method, any line break replaced by a space.</summary>
    public static string Method(HttpRequest request) => request.Method.ReplaceLineEndings(" ");

    /// <summary>The request path in its escaped form (<see cref="PathString.ToString"/>), any line break replaced by a space.</summary>
    public static string Path(HttpRequest request) => request.Path.ToString().ReplaceLineEndings(" ");
}
