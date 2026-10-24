extern alias apihost;

using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// Boots a host that is expected to refuse to start and returns the exception that stopped it.
/// <para>
/// The API runs from a top-level <c>Program</c>, so <see cref="WebApplicationFactory{TEntryPoint}"/> starts it on
/// a second thread (<c>DeferredHostBuilder</c>) and reads the started host's services from the calling one. When
/// the app's start fails (an option refused at boot, an endpoint the Result guard rejects), the app thread
/// disposes the host in its <c>finally</c> block, and a read that loses that race sees an
/// <see cref="ObjectDisposedException"/> for <c>IServiceProvider</c> instead of the error that stopped the host.
/// That lost race belongs to the test host, not to the API, and it showed up about once in fifteen full-suite
/// runs, so it is retried on a fresh host, a bounded number of times. The real boot error is returned the first
/// time it is seen; after the last attempt whatever was thrown is returned, so a test still fails loudly.
/// </para>
/// </summary>
internal static class BootFailure
{
    private const int Attempts = 3;

    /// <summary>The exception that stops the host <paramref name="boot"/> builds; each host is disposed.</summary>
    public static Task<Exception> OfAsync(Func<WebApplicationFactory<apihost::Program>> boot)
        => OfAsync(boot, static _ => Task.CompletedTask);

    /// <summary>
    /// The exception that stops the host <paramref name="boot"/> builds, or that <paramref name="thenRequest"/>
    /// raises against it, for an assertion that must hold even if a future framework defers the failing step
    /// from the start to the first request. Each host is disposed.
    /// </summary>
    public static async Task<Exception> OfAsync(
        Func<WebApplicationFactory<apihost::Program>> boot, Func<HttpClient, Task> thenRequest)
    {
        int attempt = 0;
        Exception thrown;
        do
        {
            attempt++;
            using WebApplicationFactory<apihost::Program> host = boot();
            thrown = await Should.ThrowAsync<Exception>(async () => await thenRequest(host.CreateClient()));
        }
        while (attempt < Attempts && LostTheTestHostRace(thrown));

        return thrown;
    }

    /// <summary><paramref name="exception"/> and every exception nested in it, outermost first.</summary>
    public static IEnumerable<Exception> Unwrap(Exception exception)
    {
        yield return exception;

        if (exception is AggregateException aggregate)
        {
            foreach (Exception nested in aggregate.InnerExceptions.SelectMany(Unwrap))
            {
                yield return nested;
            }
        }
        else if (exception.InnerException is { } inner)
        {
            foreach (Exception nested in Unwrap(inner))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// True when nothing in <paramref name="thrown"/> is a boot error: only the disposed-provider exception,
    /// possibly wrapped, which is what the losing side of the test host's race observes.
    /// </summary>
    private static bool LostTheTestHostRace(Exception thrown)
    {
        List<Exception> all = [.. Unwrap(thrown)];
        return all.Exists(static exception => exception is ObjectDisposedException)
            && all.TrueForAll(static exception => exception is ObjectDisposedException or AggregateException);
    }
}
