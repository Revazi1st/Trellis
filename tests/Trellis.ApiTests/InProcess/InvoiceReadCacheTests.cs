using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Api.Contracts.Invoices;
using Trellis.Domain.Invoices;
using Trellis.Persistence;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// The invoice read cache (cache-aside over HybridCache in <c>GetInvoice</c>) is correct only if every write
/// on an invoice evicts its entry. A write that forgets leaves GET answering the old state with the old
/// ETag, so a poller's <c>If-None-Match</c> is confirmed with a 304 for a version that no longer exists.
/// Each step reads first (warming the entry), writes, then reads with the previous ETag: the answer must
/// be a 200 with the new state and a new ETag, never that 304. A miss must not be cached either.
/// </summary>
public sealed class InvoiceReadCacheTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Each_write_evicts_the_cached_invoice_and_retires_its_ETag()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        Guid id = await admin.CreateInvoiceAsync();

        // Warm: a 200 with an ETag; sending that ETag back is a 304, the poller's cheap path.
        string draftTag = await ShouldServeAsync(admin, id, previousTag: null, "Draft", ct);

        (await PostAsync(admin, $"/api/v1/invoices/{id}/lines", new AddInvoiceLineRequest("Consulting", 120m), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        string withLineTag = await ShouldServeAsync(admin, id, draftTag, "Draft", ct);
        (await GetAsync(admin, id, ct)).Lines.ShouldHaveSingleItem().Description.ShouldBe("Consulting");

        (await PostAsync(admin, $"/api/v1/invoices/{id}/issue", new IssueInvoiceRequest(DateTimeOffset.UtcNow.AddDays(30)), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        string issuedTag = await ShouldServeAsync(admin, id, withLineTag, "Issued", ct);

        (await PostAsync(admin, $"/api/v1/invoices/{id}/pay", body: null, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await ShouldServeAsync(admin, id, issuedTag, "Paid", ct);
    }

    [Fact]
    public async Task Voiding_evicts_the_cached_invoice_and_retires_its_ETag()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        Guid id = await admin.CreateInvoiceAsync();
        string draftTag = await ShouldServeAsync(admin, id, previousTag: null, "Draft", ct);

        (await PostAsync(admin, $"/api/v1/invoices/{id}/void", body: null, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await ShouldServeAsync(admin, id, draftTag, "Voided", ct);
    }

    [Fact]
    public async Task A_404_is_not_cached_so_the_invoice_is_served_once_it_exists()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        // The API mints invoice ids, so a client cannot ask for one before it exists. Hiding a real invoice
        // (a soft delete straight in the database, which no endpoint's eviction sees) stands in for "not
        // there yet"; un-hiding it is its arrival.
        Guid id = await admin.CreateInvoiceAsync();
        await SetDeletedAtAsync(id, DateTimeOffset.UtcNow, ct);
        (await admin.GetAsync($"/api/v1/invoices/{id}", ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await SetDeletedAtAsync(id, deletedAtUtc: null, ct);

        (await admin.GetAsync($"/api/v1/invoices/{id}", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// GETs the invoice with <paramref name="previousTag"/> as <c>If-None-Match</c> and asserts a 200 in
    /// <paramref name="status"/> under a different ETag; then proves the new ETag earns a 304 (the new state
    /// is what is cached now). Returns the new ETag.
    /// </summary>
    private static async Task<string> ShouldServeAsync(HttpClient client, Guid id, string? previousTag, string status, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/invoices/{id}");
        if (previousTag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", previousTag);
        }

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"a write must retire ETag {previousTag}");
        InvoiceDto invoice = (await response.Content.ReadFromJsonAsync<InvoiceDto>(ct)).ShouldNotBeNull();
        invoice.Status.ToString().ShouldBe(status);
        string etag = response.Headers.ETag.ShouldNotBeNull().ToString();
        string.Equals(etag, previousTag, StringComparison.Ordinal).ShouldBeFalse($"the new state still carries ETag {etag}");

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/invoices/{id}");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using HttpResponseMessage notModified = await client.SendAsync(conditional, ct);
        notModified.StatusCode.ShouldBe(HttpStatusCode.NotModified);

        return etag;
    }

    private static async Task<InvoiceDto> GetAsync(HttpClient client, Guid id, CancellationToken ct)
        => (await client.GetFromJsonAsync<InvoiceDto>($"/api/v1/invoices/{id}", ct)).ShouldNotBeNull();

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object? body, CancellationToken ct)
        => body is null ? client.PostAsync(path, content: null, ct) : client.PostAsJsonAsync(path, body, ct);

    private async Task SetDeletedAtAsync(Guid id, DateTimeOffset? deletedAtUtc, CancellationToken ct)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoiceId = new InvoiceId(id);

        int updated = await db.Invoices
#if (UseMultitenancy)
            // No request, hence no tenant: lift the tenant filter by name as well.
            .IgnoreQueryFilters(["SoftDeleted", "Tenant"])
#else
            .IgnoreQueryFilters(["SoftDeleted"])
#endif
            .Where(invoice => invoice.Id == invoiceId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(invoice => invoice.DeletedAtUtc, deletedAtUtc), ct);

        updated.ShouldBe(1);
    }
}
