using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// What a client sees when the unit of work does not commit, driven through the real HTTP pipeline on
/// a derived host whose <see cref="IUnitOfWork"/> is replaced: an expected conflict is a 409 problem
/// through the Result seam, an unexpected fault is an opaque 500, and either way the idempotency key is
/// released so the client can retry the same request with the same key.
/// </summary>
public sealed class CommitFailureTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string ExceptionSecret = "boom: Host=db.internal;Password=hunter2";

    [Fact]
    public async Task A_concurrency_conflict_at_commit_is_a_409_problem_and_releases_the_idempotency_key()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var conflicting = factory.WithTestServices(static services => services.AddScoped<IUnitOfWork, ConflictingUnitOfWork>());
        HttpClient faulted = await factory.CreateAdminClientOnAsync(conflicting, ct);
        string key = Guid.NewGuid().ToString();

        HttpResponseMessage response = await PostCreateAsync(faulted, key, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        ProblemResponse problem = await ReadProblemAsync(response, ct);
        problem.Status.ShouldBe((int)HttpStatusCode.Conflict);
        // CustomResults writes the error code as the title until the problem-details v2 contract adds `code`.
        (problem.Code ?? problem.Title).ShouldBe(UnitOfWorkErrors.ConcurrencyConflict.Code);
        problem.Detail.ShouldBe(UnitOfWorkErrors.ConcurrencyConflict.Description);

        // The ≥400 outcome released the key: the same request with the same key now succeeds.
        HttpClient healthy = await factory.CreateAdminClientAsync(ct);
        (await PostCreateAsync(healthy, key, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_commit_that_throws_is_an_opaque_500_with_the_security_headers_and_releases_the_idempotency_key()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var throwing = factory.WithTestServices(static services => services.AddScoped<IUnitOfWork, ThrowingUnitOfWork>());
        HttpClient faulted = await factory.CreateAdminClientOnAsync(throwing, ct);
        string key = Guid.NewGuid().ToString();

        HttpResponseMessage response = await PostCreateAsync(faulted, key, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        string body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldNotContain("boom");
        body.ShouldNotContain("hunter2");
        body.ShouldNotContain(nameof(InvalidOperationException));
        ProblemResponse problem = JsonSerializer.Deserialize<ProblemResponse>(body, JsonSerializerOptions.Web)!;
        problem.Title.ShouldBe("Server failure");
        problem.Status.ShouldBe((int)HttpStatusCode.InternalServerError);

        // The exception handler clears the response before it writes the 500; the security headers survive.
        response.Headers.GetValues("X-Content-Type-Options").ShouldHaveSingleItem().ShouldBe("nosniff");
        response.Headers.GetValues("Referrer-Policy").ShouldHaveSingleItem().ShouldBe("no-referrer");
        response.Headers.GetValues("X-Frame-Options").ShouldHaveSingleItem().ShouldBe("DENY");
        response.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem().ShouldBe("frame-ancestors 'none'");

        // The exception released the key: the same request with the same key now succeeds.
        HttpClient healthy = await factory.CreateAdminClientAsync(ct);
        (await PostCreateAsync(healthy, key, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    private static async Task<ProblemResponse> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
        => (await response.Content.ReadFromJsonAsync<ProblemResponse>(JsonSerializerOptions.Web, ct)).ShouldNotBeNull();

    private static async Task<HttpResponseMessage> PostCreateAsync(HttpClient client, string idempotencyKey, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/invoices")
        {
            Content = JsonContent.Create(new CreateInvoiceRequest("Commit Co", "c@c.co", "USD")),
        };
        message.Headers.Add(ApiHeaders.IdempotencyKey, idempotencyKey);
        return await client.SendAsync(message, ct);
    }

    private sealed class ConflictingUnitOfWork : IUnitOfWork
    {
        public Task<Result> CommitAsync(CancellationToken cancellationToken)
            => Task.FromResult(Result.Failure(UnitOfWorkErrors.ConcurrencyConflict));
    }

    private sealed class ThrowingUnitOfWork : IUnitOfWork
    {
        public Task<Result> CommitAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException(ExceptionSecret);
    }
}
