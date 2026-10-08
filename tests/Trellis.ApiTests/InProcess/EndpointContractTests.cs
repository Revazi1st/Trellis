using System.Net;
using System.Net.Http.Json;
using Shouldly;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// The HTTP contracts only the real pipeline can prove: JWT auth, permission policies, RFC 9457
/// problem details, and the idempotency filter's reserve/duplicate/release behavior.
/// </summary>
public sealed class EndpointContractTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Protected_endpoint_rejects_anonymous_callers_with_401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await factory.CreateClient().GetAsync("/api/v1/invoices/overdue", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Member_without_write_permission_gets_403_on_create()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient member = await factory.CreateMemberClientAsync(ct);

        HttpResponseMessage response = await member.PostAsJsonAsync(
            "/api/v1/invoices", new CreateInvoiceRequest("ACME", "a@b.co", "USD"), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // …but their read permission works.
        (await member.GetAsync("/api/v1/invoices/overdue", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_invoice_returns_RFC9457_problem_details()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        HttpResponseMessage response = await admin.GetAsync($"/api/v1/invoices/{Guid.NewGuid()}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        string body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldContain("\"title\"");
        body.ShouldContain("\"status\"");
    }

    [Fact]
    public async Task Idempotency_key_deduplicates_and_a_failed_request_releases_its_key()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        // Reserve + succeed.
        string key = Guid.NewGuid().ToString();
        (await PostCreateAsync(admin, key, new CreateInvoiceRequest("Idempotent Inc", "i@i.co", "USD"), ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        // Same caller + route + key → duplicate, reported through the same problem seam as any endpoint error.
        HttpResponseMessage duplicate = await PostCreateAsync(admin, key, new CreateInvoiceRequest("Idempotent Inc", "i@i.co", "USD"), ct);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        ProblemResponse problem = (await duplicate.Content.ReadFromJsonAsync<ProblemResponse>(ct)).ShouldNotBeNull();
        problem.Status.ShouldBe((int)HttpStatusCode.Conflict);
        problem.Code.ShouldBe("Idempotency.Duplicate");

        // A failed command must release the key (this codebase returns Results, it doesn't throw)…
        string retriedKey = Guid.NewGuid().ToString();
        (await PostCreateAsync(admin, retriedKey, new CreateInvoiceRequest("", "", "USD"), ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // …so the client can retry the corrected request with the SAME key.
        (await PostCreateAsync(admin, retriedKey, new CreateInvoiceRequest("Retry GmbH", "r@r.de", "EUR"), ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Overlong_idempotency_key_is_a_400_not_a_500()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        HttpResponseMessage response = await PostCreateAsync(
            admin, new string('k', 200), new CreateInvoiceRequest("ACME", "a@b.co", "USD"), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Blank_idempotency_key_is_a_400_not_a_silent_opt_out()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/invoices")
        {
            Content = JsonContent.Create(new CreateInvoiceRequest("Blank Key Co", "b@k.co", "USD")),
        };
        // Whitespace, not "": HttpClient drops an empty header value before it reaches the server.
        message.Headers.TryAddWithoutValidation(ApiHeaders.IdempotencyKey, "   ").ShouldBeTrue();
        HttpResponseMessage response = await admin.SendAsync(message, ct);

        // The caller asked for the guarantee; running the command without it would be the silent failure.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        ProblemResponse problem = (await response.Content.ReadFromJsonAsync<ProblemResponse>(ct)).ShouldNotBeNull();
        problem.Code.ShouldBe("Idempotency.InvalidKey");
        problem.Detail.ShouldNotBeNull().ShouldContain("blank");
    }

    [Fact]
    public async Task Different_callers_may_reuse_the_same_idempotency_key()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        HttpClient secondAdmin = await factory.CreateAdminClientAsync(ct);

        // Two DIFFERENT users would be the sharper test, but only admins can write; two sessions of
        // the same user share scope, so route across two endpoints instead: same key, no collision.
        string key = Guid.NewGuid().ToString();
        HttpResponseMessage created = await PostCreateAsync(admin, key, new CreateInvoiceRequest("Scoped Ltd", "s@s.io", "USD"), ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid id = (await created.Content.ReadFromJsonAsync<CreateInvoiceResponse>(ct))!.Id;

        using var addLine = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/invoices/{id}/lines")
        {
            Content = JsonContent.Create(new AddInvoiceLineRequest("Line", 10m)),
        };
        addLine.Headers.Add(ApiHeaders.IdempotencyKey, key);
        (await secondAdmin.SendAsync(addLine, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task<HttpResponseMessage> PostCreateAsync(
        HttpClient client, string idempotencyKey, CreateInvoiceRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/invoices")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add(ApiHeaders.IdempotencyKey, idempotencyKey);
        return await client.SendAsync(message, ct);
    }
}
