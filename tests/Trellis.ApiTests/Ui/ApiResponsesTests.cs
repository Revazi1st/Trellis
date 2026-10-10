#if (IncludeUI)
using System.Net;
using System.Text;
using Shouldly;
using Trellis.Api.Contracts.Auth;
using Trellis.UI.Common.Services;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// What a user reads when a call fails (<c>ApiResponses.Describe</c>), driven end to end through the
/// typed client: a canned response in, <see cref="ApiResult{T}.Error"/> out. The coded bodies are the
/// shapes the API writes (ADR-0024: <c>code</c>, <c>type</c>, <c>errors[]</c> of <c>{field, code,
/// description}</c>) and the code decides; the uncoded ones are what an intermediary or an older API
/// writes, where the status and <c>detail</c> decide. Transport failures: <see cref="ApiCallTests"/>.
/// </summary>
public sealed class ApiResponsesTests
{
    private const string InvalidCredentials = "Invalid email or password.";
    private const string ServiceUnavailable = "The service is unavailable right now. Try again shortly.";

    private const string LockedOutDetail =
        "The account is temporarily locked after too many failed sign-in attempts. Try again later.";

    // Problems without a code member: title is whatever the writer chose.
    private const string LockedOutUncoded =
        """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Auth.AccountLockedOut","status":403,"detail":"The account is temporarily locked after too many failed sign-in attempts. Try again later.","traceId":"00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"}""";

    private const string WrongPasswordUncoded =
        """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.2","title":"Auth.InvalidCredentials","status":401,"detail":"The email or password is incorrect.","traceId":"00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"}""";

    private const string TwoFieldErrorsUncoded =
        """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"Validation.General","status":400,"detail":"One or more validation errors occurred.","errors":[{"code":"Email","description":"'Email' must not be empty."},{"code":"Password","description":"'Password' must not be empty."}]}""";

    private const string MalformedBody =
        """{"title":"Request.Malformed","status":400,"detail":"Failed to read parameter \"LoginRequest request\" from the request body as JSON."}""";

    private const string RateLimited =
        """{"title":"Too many requests","status":429,"detail":"Rate limit exceeded. Retry after the interval in the Retry-After header."}""";

    private const string ServerFailure =
        """{"title":"Server failure","status":500,"detail":"An unexpected error occurred."}""";

    // The coded shape: the code decides, whatever the status, title or detail say.
    private const string LockedOutCoded =
        """{"type":"urn:problem-type:Auth.AccountLockedOut","title":"Forbidden","status":403,"detail":"The account is temporarily locked after too many failed sign-in attempts. Try again later.","code":"Auth.AccountLockedOut","correlationId":"c-42"}""";

    private const string WrongPasswordCoded =
        """{"type":"urn:problem-type:Auth.InvalidCredentials","title":"Unauthorized","status":401,"detail":"The email or password is incorrect.","code":"Auth.InvalidCredentials"}""";

    private const string TwoFieldErrorsCoded =
        """{"type":"urn:problem-type:Validation.General","title":"Validation failed","status":400,"detail":"One or more validation errors occurred.","instance":"/api/v1/auth/login","code":"Validation.General","correlationId":"c-42","errors":[{"field":"email","code":"Validation.NotEmpty","description":"'Email' must not be empty."},{"field":"password","code":"Validation.NotEmpty","description":"'Password' must not be empty."}]}""";

    // A single domain error: top-level code is the error's own, errors[] holds it with a null field.
    private const string WholeRequestErrorCoded =
        """{"type":"urn:problem-type:Invoice.DueDateInPast","title":"Validation failed","status":400,"detail":"The due date is required and cannot be in the past.","instance":"/api/v1/invoices/0199b0a0-0000-7000-8000-000000000001/issue","code":"Invoice.DueDateInPast","correlationId":"c-42","errors":[{"field":null,"code":"Invoice.DueDateInPast","description":"The due date is required and cannot be in the past."}]}""";

    // Field errors and a whole-request error in one problem: every sentence, in order.
    private const string FieldAndWholeRequestErrorsCoded =
        """{"type":"urn:problem-type:Validation.General","title":"Validation failed","status":400,"detail":"One or more validation errors occurred.","code":"Validation.General","errors":[{"field":"customerName","code":"Validation.NotEmpty","description":"'Customer Name' must not be empty."},{"field":"currency","code":"Validation.ExactLength","description":"'Currency' must be 3 characters in length."},{"field":null,"code":"EmailAddress.Invalid","description":"The email address is not in a valid format."}]}""";

    private const string ForbiddenCoded =
        """{"title":"Forbidden","status":403,"detail":"The authenticated user lacks the permission this endpoint requires.","code":"Auth.Forbidden"}""";

    [Theory]
    // 403 and 400 show the server's detail.
    [InlineData(403, LockedOutUncoded, null, LockedOutDetail)]
    [InlineData(403, LockedOutCoded, null, LockedOutDetail)]
    [InlineData(400, MalformedBody, null, "Failed to read parameter \"LoginRequest request\" from the request body as JSON.")]
    // A 400 with validation errors shows the errors themselves, in order: two field errors, one
    // whole-request error (null field), and both kinds together.
    [InlineData(400, TwoFieldErrorsUncoded, null, "'Email' must not be empty. 'Password' must not be empty.")]
    [InlineData(400, TwoFieldErrorsCoded, null, "'Email' must not be empty. 'Password' must not be empty.")]
    [InlineData(400, WholeRequestErrorCoded, null, "The due date is required and cannot be in the past.")]
    [InlineData(400, FieldAndWholeRequestErrorsCoded, null,
        "'Customer Name' must not be empty. 'Currency' must be 3 characters in length. The email address is not in a valid format.")]
    // 429 turns Retry-After into a wait the user can act on.
    [InlineData(429, RateLimited, 42, "Too many requests. Try again in 42 seconds.")]
    [InlineData(429, RateLimited, 1, "Too many requests. Try again in 1 second.")]
    [InlineData(429, RateLimited, null, "Too many requests. Try again shortly.")]
    // 401: the server's sentence, the coded one, or (nothing to read) the invalid-credentials text.
    [InlineData(401, WrongPasswordUncoded, null, "The email or password is incorrect.")]
    [InlineData(401, WrongPasswordCoded, null, InvalidCredentials)]
    [InlineData(401, null, null, InvalidCredentials)]
    // A 5xx never shows its (opaque) detail.
    [InlineData(500, ServerFailure, null, ServiceUnavailable)]
    [InlineData(503, null, null, ServiceUnavailable)]
    // A code beats the detail.
    [InlineData(403, ForbiddenCoded, null, "You do not have permission to do that.")]
    [InlineData(403, null, null, "You do not have permission to do that.")]
    public async Task Describe_turns_the_response_into_the_sentence_the_user_reads(
        int status, string? problemJson, int? retryAfterSeconds, string expected)
    {
        await using var ui = new UiHarness();
        ui.Api.Always(_ => problemJson is null
            ? Responses.Status((HttpStatusCode)status)
            : Responses.Problem((HttpStatusCode)status, problemJson, retryAfterSeconds));

        ApiResult<TokenResponse> result = await ui.AuthApi.LoginAsync(
            new LoginRequest("someone@example.com", "password"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(expected);
        result.StatusCode.ShouldBe(status);
    }

    [Fact]
    public async Task Failure_keeps_the_problem_and_its_field_errors_for_the_caller()
    {
        await using var ui = new UiHarness();
        ui.Api.Always(_ => Responses.Problem(HttpStatusCode.BadRequest, TwoFieldErrorsCoded));

        ApiResult<TokenResponse> result = await ui.AuthApi.LoginAsync(
            new LoginRequest(string.Empty, string.Empty), TestContext.Current.CancellationToken);

        result.Problem.ShouldNotBeNull();
        result.Problem.Code.ShouldBe("Validation.General");
        result.Problem.CorrelationId.ShouldBe("c-42");
        result.Errors.Select(static e => (e.Field, e.Code)).ShouldBe(
            [("email", "Validation.NotEmpty"), ("password", "Validation.NotEmpty")]);
    }

    [Fact]
    public async Task Whole_request_error_keeps_its_null_field()
    {
        await using var ui = new UiHarness();
        ui.Api.Always(_ => Responses.Problem(HttpStatusCode.BadRequest, WholeRequestErrorCoded));

        ApiResult<TokenResponse> result = await ui.AuthApi.LoginAsync(
            new LoginRequest("someone@example.com", "password"), TestContext.Current.CancellationToken);

        result.Problem!.Code.ShouldBe("Invoice.DueDateInPast");
        result.Errors.ShouldHaveSingleItem().Field.ShouldBeNull();
    }

    [Fact]
    public async Task Failure_without_a_problem_body_has_no_problem_and_no_errors()
    {
        await using var ui = new UiHarness();
        ui.Api.Always(static _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>upstream down</html>", Encoding.UTF8, "text/html"),
        });

        ApiResult<TokenResponse> result = await ui.AuthApi.LoginAsync(
            new LoginRequest("someone@example.com", "password"), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(ServiceUnavailable);
        result.StatusCode.ShouldBe(502);
        result.Problem.ShouldBeNull();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task Register_conflict_shows_the_server_sentence()
    {
        await using var ui = new UiHarness();
        ui.Api.Always(static _ => Responses.Problem(
            HttpStatusCode.Conflict,
            """{"title":"Auth.EmailAlreadyInUse","status":409,"detail":"An account with this email already exists."}"""));

        ApiResult<Guid> result = await ui.AuthApi.RegisterAsync(
            new RegisterRequest("taken@example.com", "Passw0rd!"), TestContext.Current.CancellationToken);

        result.Error.ShouldBe("An account with this email already exists.");
        result.StatusCode.ShouldBe(409);
    }
}
#endif
