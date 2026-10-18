using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Shouldly;
using Trellis.Api.Common.Http;
using Trellis.Persistence.Identity;
using Trellis.SharedKernel.Decorators;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// An authenticated request names its caller on every signal (<see cref="UserTelemetryMiddleware"/>): the
/// use-case outcome record inherits the <c>CallerUserId</c> log scope, and the request span around the use case
/// carries <c>enduser.id</c>. Both are read where an OTLP exporter reads them, from processors on the
/// OpenTelemetry logger and tracer providers. The id is in no message, only in the scope, and a scope
/// reaches the exported record only because ServiceDefaults turns <c>IncludeScopes</c> on, so these tests
/// pin that as well. An anonymous request carries neither.
/// </summary>
public sealed class UserTelemetryTests(UserTelemetryTests.Factory factory) : IClassFixture<UserTelemetryTests.Factory>
{
    [Fact]
    public async Task An_authenticated_requests_outcome_record_and_request_span_carry_the_callers_user_id()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        string adminId = await factory.AdminIdAsync();

        // Signing out everywhere: a use case only a signed-in caller can run, and one every project keeps.
        using HttpResponseMessage response = await admin.PostAsync("/api/v1/auth/revoke-all", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        ExportedLog outcome = factory.OutcomesOf("RevokeSessionsCommand").ShouldHaveSingleItem();
        outcome.Fields.ShouldContain(new KeyValuePair<string, string?>(UserTelemetryMiddleware.ScopeProperty, adminId));
        outcome.Fields.ShouldNotContain(static field => field.Value != null && field.Value.Contains(ApiFactory.AdminEmail, StringComparison.OrdinalIgnoreCase));

        UseCaseSpan span = factory.SpansOf("RevokeSessionsCommand").Where(span => span.TraceId == outcome.TraceId).ShouldHaveSingleItem();
        span.ParentSource.ShouldBe("Microsoft.AspNetCore");
        span.ParentEndUserId.ShouldBe(adminId);
    }

    [Fact]
    public async Task An_anonymous_requests_outcome_record_and_request_span_carry_no_user_id()
    {
        // Signing in is anonymous: the login use case runs before the caller holds a token.
        await factory.LoginAsAdminAsync(TestContext.Current.CancellationToken);

        ExportedLog[] outcomes = factory.OutcomesOf("LoginCommand");
        outcomes.ShouldNotBeEmpty();
        // The request's other scopes are there, so the missing user id is not merely missing scopes.
        outcomes.ShouldAllBe(static outcome => outcome.Fields.Any(static field => field.Key == CorrelationIdMiddleware.ItemKey));
        outcomes.ShouldAllBe(static outcome => outcome.Fields.All(static field => field.Key != UserTelemetryMiddleware.ScopeProperty));

        HashSet<ActivityTraceId> traces = [.. outcomes.Select(static outcome => outcome.TraceId)];
        UseCaseSpan[] spans = [.. factory.SpansOf("LoginCommand").Where(span => traces.Contains(span.TraceId))];
        spans.ShouldNotBeEmpty();
        spans.ShouldAllBe(static span => span.ParentSource == "Microsoft.AspNetCore" && span.ParentEndUserId == null);
    }

    /// <summary>
    /// One exported record, copied out (OpenTelemetry pools <see cref="LogRecord"/>s): its event id, text, trace,
    /// and <see cref="Fields"/>, what an OTLP exporter writes as its attributes.
    /// </summary>
    public sealed record ExportedLog(int EventId, string Message, ActivityTraceId TraceId, IReadOnlyList<KeyValuePair<string, string?>> Fields);

    /// <summary>One ended use-case span, its trace, and what its parent, the request span, carried when it ended.</summary>
    public sealed record UseCaseSpan(string Name, ActivityTraceId TraceId, string? ParentSource, string? ParentEndUserId);

    /// <summary><see cref="ApiFactory"/> with a capturing processor on the OpenTelemetry logger and tracer providers.</summary>
    public sealed class Factory : ApiFactory
    {
        private readonly CapturingLogProcessor _logs = new();
        private readonly UseCaseSpanProcessor _spans = new();

        /// <summary>The use-case outcome records (event ids 1001–1099) of one use case.</summary>
        public ExportedLog[] OutcomesOf(string useCase)
            => [.. _logs.Records.Where(record => record.EventId is >= 1001 and <= 1099
                                                 && record.Message.Contains(useCase, StringComparison.Ordinal))];

        public UseCaseSpan[] SpansOf(string useCase) => [.. _spans.Spans.Where(span => span.Name == useCase)];

        public async Task<string> AdminIdAsync()
        {
            using IServiceScope scope = Services.CreateScope();
            ApplicationUser? admin = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(AdminEmail);
            return admin.ShouldNotBeNull().Id.ToString();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureOpenTelemetryLoggerProvider(logging => logging.AddProcessor(_logs));
                services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(_spans));
            });
        }
    }

    private sealed class CapturingLogProcessor : BaseProcessor<LogRecord>
    {
        private readonly ConcurrentQueue<ExportedLog> _records = new();

        public IReadOnlyList<ExportedLog> Records => [.. _records];

        public override void OnEnd(LogRecord data)
        {
            // The record's own fields and, with IncludeScopes, every scope's entries: the attributes an OTLP
            // exporter writes. (A logging bridge such as Serilog's folds the scopes into the event's properties
            // before handing it on, so there they arrive among the record's own fields.)
            List<KeyValuePair<string, string?>> fields = [.. (data.Attributes ?? []).Select(static field => Text(field))];
            data.ForEachScope(
                static (scope, entries) =>
                {
                    foreach (KeyValuePair<string, object?> entry in scope)
                    {
                        entries.Add(Text(entry));
                    }
                },
                fields);

            _records.Enqueue(new ExportedLog(data.EventId.Id, data.FormattedMessage ?? data.Body ?? string.Empty, data.TraceId, fields));
        }

        private static KeyValuePair<string, string?> Text(KeyValuePair<string, object?> field)
            => new(field.Key, Convert.ToString(field.Value, CultureInfo.InvariantCulture));
    }

    private sealed class UseCaseSpanProcessor : BaseProcessor<Activity>
    {
        private readonly ConcurrentQueue<UseCaseSpan> _spans = new();

        public IReadOnlyList<UseCaseSpan> Spans => [.. _spans];

        public override void OnEnd(Activity data)
        {
            // Activity listeners are process-wide, so this sees the use cases of every host the test run has
            // open; callers pick theirs by trace id. The request span is still open when its use case ends,
            // so its tags are read from the live parent.
            if (data.Source.Name == TracingDecorators.ActivitySourceName)
            {
                _spans.Enqueue(new UseCaseSpan(
                    data.OperationName,
                    data.TraceId,
                    data.Parent?.Source.Name,
                    data.Parent?.GetTagItem(UserTelemetryMiddleware.SpanAttribute) as string));
            }
        }
    }
}
