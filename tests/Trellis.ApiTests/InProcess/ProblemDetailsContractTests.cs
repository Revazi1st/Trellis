using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Trellis.Api.Common.Hardening;
using Trellis.Api.Common.Http;
using Trellis.Api.Common.Results;
using Trellis.Api.Contracts.Auth;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Results;
using Xunit;
using WireFieldError = Trellis.Api.Contracts.Common.FieldError;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// The problem-details contract (ADR-0024), one status at a time, through the real pipeline: every 4xx/5xx
/// is <c>application/problem+json</c> deserialisable into the Contracts' <see cref="ProblemResponse"/>
/// (that deserialisation is the proof the client mirror matches the wire) and carries <c>code</c>,
/// <c>type == urn:problem-type:{code}</c>, a human <c>title</c>, <c>instance</c> (the request path),
/// <c>traceId</c> and a <c>correlationId</c> equal to the <c>X-Correlation-ID</c> header — whichever of
/// the writers produced it: <c>CustomResults</c>, the authentication callbacks, the rate limiter, the
/// exception handlers or the status-code pages. The 413 needs a real Kestrel listener (TestServer does not
/// enforce body limits), hence the second fixture.
/// </summary>
public sealed class ProblemDetailsContractTests(ApiFactory factory, RequestLimitTests.KestrelApiFactory kestrel)
    : IClassFixture<ApiFactory>, IClassFixture<RequestLimitTests.KestrelApiFactory>
{
    private const string ExceptionSecret = "boom: Host=db.internal;Password=hunter2";

    [Fact]
    public async Task Validation_failure_lists_each_field_with_its_json_path_and_rule_code()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/v1/invoices", new CreateInvoiceRequest(CustomerName: "", CustomerEmail: "", Currency: "USD"), ct);

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "Validation.General", "/api/v1/invoices", ct);
        problem.Title.ShouldBe("Validation failed");
        IReadOnlyList<WireFieldError> errors = problem.Errors.ShouldNotBeNull();
        errors.Select(static e => (e.Field, e.Code)).ShouldBe(
            [("customerName", "Validation.NotEmpty"), ("customerEmail", "Validation.NotEmpty")], ignoreOrder: true);
        errors.ShouldAllBe(static e => !string.IsNullOrWhiteSpace(e.Description));
    }

    [Fact]
    public async Task Value_object_errors_name_the_request_field_they_came_from()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        // Passes the shape validator; the value objects reject both, combined into one 400, each under its input.
        HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/v1/invoices", new CreateInvoiceRequest("ACME", "a@b", "123"), ct);

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "Validation.General", "/api/v1/invoices", ct);
        problem.Errors.ShouldNotBeNull().Select(static e => (e.Field, e.Code)).ShouldBe(
            [("customerEmail", "EmailAddress.Invalid"), ("currency", "Money.InvalidCurrency")], ignoreOrder: true);
    }

    [Fact]
    public async Task A_single_value_object_error_keeps_its_own_code_and_names_its_field()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        // "a@b" passes the shape validator and the UI's [EmailAddress]; only the value object rejects it.
        HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/v1/invoices", new CreateInvoiceRequest("ACME", "a@b", "USD"), ct);

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "EmailAddress.Invalid", "/api/v1/invoices", ct);
        WireFieldError only = problem.Errors.ShouldNotBeNull().ShouldHaveSingleItem();
        only.Field.ShouldBe("customerEmail");
        only.Code.ShouldBe("EmailAddress.Invalid");
        only.Description.ShouldBe(problem.Detail);
    }

    [Fact]
    public async Task An_aggregate_rule_a_handler_attributes_names_its_field()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        Guid id = await CreateInvoiceAsync(admin, ct);
        (await admin.PostAsJsonAsync($"/api/v1/invoices/{id}/lines", new AddInvoiceLineRequest("Design", 10m), ct))
            .IsSuccessStatusCode.ShouldBeTrue();

        HttpResponseMessage response = await admin.PostAsJsonAsync(
            $"/api/v1/invoices/{id}/issue", new IssueInvoiceRequest(factory.Time.GetUtcNow().AddDays(-1)), ct);

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, InvoiceErrors.DueDateInPast.Code, $"/api/v1/invoices/{id}/issue", ct);
        WireFieldError only = problem.Errors.ShouldNotBeNull().ShouldHaveSingleItem();
        only.Field.ShouldBe("dueDate");
        only.Code.ShouldBe(InvoiceErrors.DueDateInPast.Code);
    }

    [Fact]
    public async Task A_single_domain_validation_error_still_carries_errors()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        Guid id = await CreateInvoiceAsync(admin, ct);

        // A draft with no lines cannot be issued: one plain Error.Validation from the aggregate, about the
        // request as a whole, so the handler attributes it to no field.
        HttpResponseMessage response = await admin.PostAsJsonAsync(
            $"/api/v1/invoices/{id}/issue", new IssueInvoiceRequest(DateTimeOffset.UtcNow.AddDays(7)), ct);

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, InvoiceErrors.NoLines.Code, $"/api/v1/invoices/{id}/issue", ct);
        problem.Title.ShouldBe("Validation failed");
        problem.Detail.ShouldBe(InvoiceErrors.NoLines.Description);
        WireFieldError only = problem.Errors.ShouldNotBeNull().ShouldHaveSingleItem();
        only.Field.ShouldBeNull();
        only.Code.ShouldBe(InvoiceErrors.NoLines.Code);
        only.Description.ShouldBe(InvoiceErrors.NoLines.Description);
    }

    [Fact]
    public async Task Malformed_json_body_is_a_400_problem_not_a_500()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        HttpResponseMessage response = await admin.PostAsync(
            "/api/v1/invoices", new StringContent("{ not json", Encoding.UTF8, "application/json"), ct);

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "Request.Malformed", "/api/v1/invoices", ct);
        problem.Detail.ShouldNotBeNullOrWhiteSpace();
        problem.Errors.ShouldBeNull();
    }

    [Fact]
    public async Task Malformed_json_body_is_a_400_problem_in_production_too()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // Outside Development the framework would answer a BODYLESS 400 unless ThrowOnBadRequest is forced on.
        using var production = factory.WithWebHostBuilder(static builder => builder.UseEnvironment(Environments.Production));
        HttpClient admin = await factory.CreateAdminClientOnAsync(production, ct);

        HttpResponseMessage response = await admin.PostAsync(
            "/api/v1/invoices", new StringContent("{ not json", Encoding.UTF8, "application/json"), ct);

        await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "Request.Malformed", "/api/v1/invoices", ct);
    }

    [Fact]
    public async Task Unparsable_route_id_is_a_400_problem()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        HttpResponseMessage response = await admin.GetAsync("/api/v1/invoices/not-a-guid", ct);

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "Request.Malformed", "/api/v1/invoices/not-a-guid", ct);
        problem.Detail.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Anonymous_request_is_a_401_problem_with_a_bearer_challenge()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await factory.CreateClient().GetAsync("/api/v1/invoices/overdue", ct);

        await ShouldBeProblemAsync(response, HttpStatusCode.Unauthorized, "Auth.Unauthenticated", "/api/v1/invoices/overdue", ct);
        response.Headers.WwwAuthenticate.ShouldHaveSingleItem().Scheme.ShouldBe("Bearer");
    }

    [Fact]
    public async Task Rejected_token_is_a_401_problem_naming_invalid_token()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.token");

        HttpResponseMessage response = await client.GetAsync("/api/v1/invoices/overdue", ct);

        await ShouldBeProblemAsync(response, HttpStatusCode.Unauthorized, "Auth.Unauthenticated", "/api/v1/invoices/overdue", ct);
        AuthenticationHeaderValue challenge = response.Headers.WwwAuthenticate.ShouldHaveSingleItem();
        challenge.Scheme.ShouldBe("Bearer");
        challenge.Parameter.ShouldNotBeNull().ShouldContain("error=\"invalid_token\"");
    }

    [Fact]
    public async Task Locked_out_account_is_a_403_problem()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = factory.CreateClient();
        string email = $"lockout-{Guid.NewGuid():N}@inprocess.test";
        (await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, ApiFactory.AdminPassword), ct))
            .EnsureSuccessStatusCode();

        // Identity locks the account on the fifth failure; the sixth attempt is a 403 whatever the password.
        HttpResponseMessage response = null!;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "wrong-password"), ct);
        }

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.Forbidden, "Auth.AccountLockedOut", "/api/v1/auth/login", ct);
        problem.Title.ShouldBe("Forbidden");
        problem.Detail.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Member_without_permission_is_a_403_problem()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient member = await factory.CreateMemberClientAsync(ct);

        HttpResponseMessage response = await member.PostAsJsonAsync(
            "/api/v1/invoices", new CreateInvoiceRequest("ACME", "a@b.co", "USD"), ct);

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.Forbidden, "Auth.Forbidden", "/api/v1/invoices", ct);
        problem.Title.ShouldBe("Forbidden");
    }

    [Fact]
    public async Task Unknown_invoice_is_a_404_problem()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        var id = Guid.NewGuid();

        HttpResponseMessage response = await admin.GetAsync($"/api/v1/invoices/{id}", ct);

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.NotFound, "Invoice.NotFound", $"/api/v1/invoices/{id}", ct);
        problem.Title.ShouldBe("Not found");
        problem.Detail.ShouldNotBeNull().ShouldContain(id.ToString());
    }

    [Fact]
    public async Task Unknown_route_is_a_404_problem()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        HttpResponseMessage response = await admin.GetAsync("/api/v1/nothing-here", ct);

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.NotFound, "Http.NotFound", "/api/v1/nothing-here", ct);
        problem.Title.ShouldBe("Not found");
    }

    [Fact]
    public async Task Wrong_method_is_a_405_problem_that_keeps_the_allow_header()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        var id = Guid.NewGuid();

        HttpResponseMessage response = await admin.DeleteAsync($"/api/v1/invoices/{id}", ct);

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.MethodNotAllowed, "Http.MethodNotAllowed", $"/api/v1/invoices/{id}", ct);
        problem.Title.ShouldBe("Method not allowed");
        response.Content.Headers.Allow.Contains("GET", StringComparer.OrdinalIgnoreCase).ShouldBeTrue();
    }

    [Fact]
    public async Task Reused_idempotency_key_with_a_different_body_is_a_422_problem()
    {
        // A retry with the SAME body replays the stored 201 and is no problem at all; the idempotency 409s —
        // in flight, not replayable — need a race or a derived host, so IdempotencyReplayTests asserts their shape.
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        string key = Guid.NewGuid().ToString();
        (await PostCreateAsync(admin, key, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        HttpResponseMessage response = await PostCreateAsync(admin, key, ct, customerName: "Reused Key Co");

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.UnprocessableEntity, "Idempotency.FingerprintMismatch", "/api/v1/invoices", ct);
        problem.Title.ShouldBe("Unprocessable request");
        problem.Errors.ShouldBeNull();
    }

    [Fact]
    public async Task Oversized_body_is_a_413_problem()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string oversizedEmail = new('a', (int)ApiHardeningExtensions.DefaultMaxRequestBodySize + 1);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(oversizedEmail, "x")),
        };
        // Kestrel answers before the body streams (see RequestLimitTests): the client never races a reset.
        request.Headers.ExpectContinue = true;

        HttpResponseMessage response = await kestrel.CreateClient().SendAsync(request, ct);

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "Request.TooLarge", "/api/v1/auth/login", ct);
        problem.Title.ShouldBe("Request too large");
    }

    [Fact]
    public async Task Non_json_content_type_is_a_415_problem()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        HttpResponseMessage response = await admin.PostAsync(
            "/api/v1/invoices", new StringContent("customerName=ACME", Encoding.UTF8, "text/plain"), ct);

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.UnsupportedMediaType, "Request.UnsupportedMediaType", "/api/v1/invoices", ct);
        problem.Title.ShouldBe("Unsupported media type");
    }

    [Fact]
    public async Task Throttled_request_is_a_429_problem_with_retry_after()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = factory.CreateClient();   // its own address, hence its own auth partition

        HttpResponseMessage response = null!;
        for (int attempt = 0; attempt < 11; attempt++)
        {
            response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("nobody@nowhere.dev", "wrong-password"), ct);
        }

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.TooManyRequests, "RateLimit.Exceeded", "/api/v1/auth/login", ct);
        problem.Title.ShouldBe("Too many requests");
        TimeSpan retryAfter = response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldNotBeNull();
        retryAfter.ShouldBeGreaterThan(TimeSpan.Zero);
        problem.Detail.ShouldNotBeNull().ShouldContain($"{Math.Ceiling(retryAfter.TotalSeconds)} seconds");
    }

    [Fact]
    public async Task Unhandled_exception_is_an_opaque_500_problem()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var throwing = factory.WithTestServices(static services => services.AddScoped<IUnitOfWork, ThrowingUnitOfWork>());
        HttpClient admin = await factory.CreateAdminClientOnAsync(throwing, ct);

        HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/v1/invoices", new CreateInvoiceRequest("Boom Co", "b@b.co", "USD"), ct);

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.InternalServerError, "Server.Failure", "/api/v1/invoices", ct);
        problem.Title.ShouldBe("Server failure");
        problem.Detail.ShouldBe("An unexpected error occurred.");
        string body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldNotContain("boom");
        body.ShouldNotContain("hunter2");
        body.ShouldNotContain(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task A_failure_result_is_a_500_that_keeps_its_code_and_says_nothing_specific()
    {
        // A Failure's description can carry what a connection or a third party said; the 500 keeps the
        // code, so the failure stays identifiable, and gives the catalogue sentence instead (ADR-0024 rule 4).
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var failing = factory.WithTestServices(static services => services.AddScoped<IUnitOfWork>(
            static _ => new FailingUnitOfWork(Error.Failure("Storage.Unreachable", ExceptionSecret))));
        HttpClient admin = await factory.CreateAdminClientOnAsync(failing, ct);

        HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/v1/invoices", new CreateInvoiceRequest("Leaky Co", "l@l.co", "USD"), ct);

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.InternalServerError, "Storage.Unreachable", "/api/v1/invoices", ct);
        problem.Title.ShouldBe("Server failure");
        problem.Detail.ShouldBe(ProblemCatalog.ServerFailure.Detail);
        problem.Errors.ShouldBeNull();
        string body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldNotContain("boom");
        body.ShouldNotContain("hunter2");
    }

    [Fact]
    public async Task An_unavailable_result_is_a_503_that_keeps_its_code_and_only_says_to_retry()
    {
        // A dependency that is down for now (ErrorType.Unavailable): a gateway or a client can tell it from
        // a bug by status alone, and the detail is fixed like every 5xx's.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var unavailable = factory.WithTestServices(static services => services.AddScoped<IUnitOfWork>(
            static _ => new FailingUnitOfWork(Error.Unavailable("Storage.Unavailable", ExceptionSecret))));
        HttpClient admin = await factory.CreateAdminClientOnAsync(unavailable, ct);

        HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/v1/invoices", new CreateInvoiceRequest("Down Co", "d@d.co", "USD"), ct);

        ProblemResponse problem = await ShouldBeProblemAsync(response, HttpStatusCode.ServiceUnavailable, "Storage.Unavailable", "/api/v1/invoices", ct);
        problem.Title.ShouldBe("Service unavailable");
        problem.Detail.ShouldBe(CustomResults.UnavailableDetail);
        problem.Errors.ShouldBeNull();
        (await response.Content.ReadAsStringAsync(ct)).ShouldNotContain("hunter2");
    }

    [Fact]
    public async Task An_endpoint_returning_a_result_unmapped_fails_fast_instead_of_answering_200()
    {
        // Without the guard this successful Result answered 200 with {"isSuccess":true,…,"value":42} and a
        // failed one a 500: the endpoint must map it (TypedResults / CustomResults.Problem), never return it.
        // With it the host refuses to start, because minimal APIs resolve the response's JSON contract when
        // they build the endpoint; the request is there in case a future framework defers that to the call.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var host = factory.WithTestServices(static services => services.AddTransient<IStartupFilter, UnmappedResultEndpoint>());

        Exception thrown = await Should.ThrowAsync<Exception>(async () => await host.CreateClient().GetAsync(UnmappedResultEndpoint.Route, ct));

        // ToString carries every inner exception: the guard's NotSupportedException names the fix.
        string report = thrown.ToString();
        report.ShouldContain(nameof(NotSupportedException));
        report.ShouldContain("CustomResults.Problem");
    }

    /// <summary>The contract every problem honours, whichever writer produced it.</summary>
    private static async Task<ProblemResponse> ShouldBeProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string code, string instance, CancellationToken ct)
    {
        response.StatusCode.ShouldBe(status);
        response.ShouldBeProblemJson();

        string body = await response.Content.ReadAsStringAsync(ct);
        ProblemResponse problem = JsonSerializer.Deserialize<ProblemResponse>(body, JsonSerializerOptions.Web).ShouldNotBeNull(body);

        problem.Status.ShouldBe((int)status, body);
        problem.Code.ShouldBe(code, body);
        problem.Type.ShouldBe(ProblemTypes.For(code), body);
        problem.Instance.ShouldBe(instance, body);
        problem.TraceId.ShouldNotBeNullOrWhiteSpace(body);
        problem.CorrelationId.ShouldBe(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single(), body);

        // A human title: a phrase, not the code, not a sentence.
        string title = problem.Title.ShouldNotBeNull(body);
        title.ShouldNotBeNullOrWhiteSpace(body);
        string.Equals(title, code, StringComparison.Ordinal).ShouldBeFalse(body);
        title.Contains('.', StringComparison.Ordinal).ShouldBeFalse(body);

        return problem;
    }

    private static async Task<Guid> CreateInvoiceAsync(HttpClient client, CancellationToken ct)
    {
        HttpResponseMessage created = await client.PostAsJsonAsync(
            "/api/v1/invoices", new CreateInvoiceRequest("Contract Co", "c@c.co", "USD"), ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<CreateInvoiceResponse>(ct))!.Id;
    }

    private static async Task<HttpResponseMessage> PostCreateAsync(HttpClient client, string idempotencyKey, CancellationToken ct, string customerName = "Idempotent Co")
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/invoices")
        {
            Content = JsonContent.Create(new CreateInvoiceRequest(customerName, "i@i.co", "USD")),
        };
        message.Headers.Add(ApiHeaders.IdempotencyKey, idempotencyKey);
        return await client.SendAsync(message, ct);
    }

    private sealed class ThrowingUnitOfWork : IUnitOfWork
    {
        public Task<Result> CommitAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException(ExceptionSecret);
    }

    /// <summary>A commit that reports <paramref name="error"/> as its Result: the path a Result-produced 5xx takes.</summary>
    private sealed class FailingUnitOfWork(Error error) : IUnitOfWork
    {
        public Task<Result> CommitAsync(CancellationToken cancellationToken) => Task.FromResult(Result.Failure(error));
    }

    /// <summary>
    /// A throwaway endpoint that makes the mistake the JSON guard exists for: it returns
    /// <c>Task&lt;Result&lt;int&gt;&gt;</c> instead of mapping it. Mapped in a branch of its own ahead of the
    /// app's pipeline, so the guard's exception reaches the test instead of the exception handler.
    /// </summary>
    private sealed class UnmappedResultEndpoint : IStartupFilter
    {
        public const string Route = "/__test/unmapped-result/value";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map("/__test/unmapped-result", static branch =>
            {
                branch.UseRouting();
                branch.UseEndpoints(static endpoints => endpoints
                    .MapGet("/value", static async Task<Result<int>> () =>
                    {
                        await Task.Yield();
                        return Result.Success(42);
                    }));
            });

            next(app);
        };
    }
}
