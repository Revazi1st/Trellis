using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Trellis.Api.Common.Http;
using Trellis.Api.Common.Idempotency;
using Trellis.ApiTests.InProcess;
using Trellis.Application.Abstractions;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// Three log lines name a request the host could not answer normally: the malformed-request line
/// (Information), the unhandled-exception line (Error) and the idempotency residue (Warning). Their
/// method and path are the client's, so a line break in either would end the line and start a forged
/// one (CWE-117, CodeQL's <c>cs/log-forging</c>). Kestrel never lets one through — a method is token
/// characters only, a path is logged escaped — which is why the forged values are set on a bare
/// <see cref="DefaultHttpContext"/>: each line must stay one line whatever server or test produced the
/// request, and must still name it. The sanitiser is <see cref="LogSafeRequestLine"/>.
/// </summary>
public sealed class LogSafeRequestLineTests
{
    /// <summary>CRLF inside the method: the header-splitting payload, which no Kestrel request can carry.</summary>
    private const string ForgedMethod = "GET\r\nforged";

    /// <summary>A line feed inside the decoded path, which <see cref="PathString"/> renders as <c>%0A</c>.</summary>
    private static readonly PathString ForgedPath = new("/a\nb");

    [Fact]
    public async Task The_malformed_request_line_stays_one_line_and_names_the_request()
    {
        var logs = new AuthFlowTests.CapturingLoggerProvider();
        await using ServiceProvider services = HostPlumbing(logs);
        DefaultHttpContext http = ForgedRequest(services);
        var handler = new BadHttpRequestExceptionHandler(services.GetRequiredService<ILogger<BadHttpRequestExceptionHandler>>());
        var malformed = new BadHttpRequestException("Failed to read parameter \"Guid id\" from the request path.", StatusCodes.Status400BadRequest);

        bool handled = await handler.TryHandleAsync(http, malformed, TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        ShouldBeOneLineNamingTheRequest(TheLineAt(logs, LogLevel.Information));
    }

    [Fact]
    public async Task The_unhandled_exception_line_stays_one_line_and_names_the_request()
    {
        var logs = new AuthFlowTests.CapturingLoggerProvider();
        await using ServiceProvider services = HostPlumbing(logs);
        DefaultHttpContext http = ForgedRequest(services);
        var handler = new ProblemDetailsExceptionHandler(services.GetRequiredService<ILogger<ProblemDetailsExceptionHandler>>());

        bool handled = await handler.TryHandleAsync(http, new InvalidOperationException("boom"), TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        ShouldBeOneLineNamingTheRequest(TheLineAt(logs, LogLevel.Error));
    }

    [Fact]
    public async Task The_idempotency_residue_line_stays_one_line_and_names_the_request()
    {
        var logs = new AuthFlowTests.CapturingLoggerProvider();
        await using ServiceProvider services = HostPlumbing(logs);
        DefaultHttpContext http = ForgedRequest(services);

        await CompletingResult.RecordAsync(http, new UnreachableIdempotencyStore(), "key", StoredResponse.Unavailable(StatusCodes.Status500InternalServerError));

        ShouldBeOneLineNamingTheRequest(TheLineAt(logs, LogLevel.Warning));
    }

    /// <summary>One line, with the method's two tokens and the escaped path still in it: only the break went, nothing else.</summary>
    private static void ShouldBeOneLineNamingTheRequest(string line)
    {
        line.ShouldNotContain("\r");
        line.ShouldNotContain("\n");
        line.ShouldContain("GET forged", Case.Sensitive);
        line.ShouldContain("/a%0Ab", Case.Sensitive);
    }

    private static string TheLineAt(AuthFlowTests.CapturingLoggerProvider logs, LogLevel level)
        => logs.All.Where(entry => entry.Level == level).Select(static entry => entry.Message).ShouldHaveSingleItem();

    /// <summary>What the handlers resolve from the request: a logger, and the problem-details writer the host registers.</summary>
    private static ServiceProvider HostPlumbing(ILoggerProvider logs)
        => new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(logs))
            .AddProblemDetails()
            .BuildServiceProvider();

    private static DefaultHttpContext ForgedRequest(IServiceProvider services)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = ForgedMethod;
        http.Request.Path = ForgedPath;
        return http;
    }

    /// <summary>The completing write fails, as it does when the database is gone right after the commit: the residue the Warning is about.</summary>
    private sealed class UnreachableIdempotencyStore : IIdempotencyStore
    {
        public Task<IdempotencyLookup> FindAsync(string key, CancellationToken cancellationToken) => Task.FromResult(IdempotencyLookup.None);

        public void Reserve(string key, string requestFingerprint)
        {
            // Nothing to track: the test calls the completing write directly.
        }

        public Task CompleteAsync(string key, StoredResponse response, CancellationToken cancellationToken)
            => Task.FromException(new InvalidOperationException("the idempotency table is unreachable"));
    }
}
