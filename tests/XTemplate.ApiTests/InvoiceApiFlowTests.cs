using System.Net;
using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Shouldly;
using XTemplate.Api.Contracts.Invoices;
using Xunit;

namespace XTemplate.ApiTests;

/// <summary>
/// True end-to-end: boots the real AppHost (PostgreSQL container, migration worker, API) with
/// randomized ports and drives the public HTTP contract exactly like a client would.
///
/// Requires Aspire's orchestrator (DCP), which runs project resources as local processes alongside
/// container resources — so it needs a real host environment: a local .NET SDK, or CI. It cannot run
/// from inside the SDK container (project processes there can't reach the sibling database container),
/// so it is opt-in: set RUN_ASPIRE_E2E=1. The containerized `dotnet test` skips it and stays green.
/// </summary>
public sealed class InvoiceApiFlowTests
{
    private static bool E2EEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_ASPIRE_E2E"), "1", StringComparison.Ordinal);

    [Fact]
    public async Task Invoice_lifecycle_and_error_contracts_work_end_to_end()
    {
        Assert.SkipUnless(E2EEnabled, "Aspire orchestration E2E test — set RUN_ASPIRE_E2E=1 (needs a local SDK or CI, not the SDK container).");

        CancellationToken ct = TestContext.Current.CancellationToken;

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.XTemplate_AppHost>(ct);
        await using DistributedApplication app = await appHost.BuildAsync(ct);
        await app.StartAsync(ct);

        // Healthy implies the migration worker already completed (api WaitsForCompletion on it).
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", ct);
        using HttpClient client = app.CreateHttpClient("api");

        // create draft
        HttpResponseMessage createResponse = await client.PostAsJsonAsync(
            "/api/v1/invoices",
            new CreateInvoiceRequest("ACME Corp", "billing@acme.com", "USD"),
            ct);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        createResponse.Headers.Location.ShouldNotBeNull();
        Guid id = (await createResponse.Content.ReadFromJsonAsync<CreateInvoiceResponse>(ct))!.Id;

        // add lines
        (await client.PostAsJsonAsync($"/api/v1/invoices/{id}/lines", new AddInvoiceLineRequest("Design", 150.50m), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync($"/api/v1/invoices/{id}/lines", new AddInvoiceLineRequest("Build", 349.50m), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // issue with a past due date so it shows up as overdue
        (await client.PostAsJsonAsync($"/api/v1/invoices/{id}/issue",
                new IssueInvoiceRequest(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // read back the full aggregate
        InvoiceDto invoice = (await client.GetFromJsonAsync<InvoiceDto>($"/api/v1/invoices/{id}", ct))!;
        invoice.Status.ShouldBe("Issued");
        invoice.Total.ShouldBe(500.00m);
        invoice.Lines.Count.ShouldBe(2);

        // overdue listing includes it (plus the seeded sample)
        IReadOnlyList<InvoiceDto> overdue =
            (await client.GetFromJsonAsync<IReadOnlyList<InvoiceDto>>("/api/v1/invoices/overdue", ct))!;
        overdue.ShouldContain(i => i.Id == id);
        overdue.ShouldContain(i => i.CustomerName == "Overdue Industries");   // seeded by the migration worker

        // pay
        (await client.PostAsync($"/api/v1/invoices/{id}/pay", content: null, ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // error contracts
        HttpResponseMessage notFound = await client.GetAsync($"/api/v1/invoices/{Guid.NewGuid()}", ct);
        notFound.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        HttpResponseMessage badRequest = await client.PostAsJsonAsync(
            "/api/v1/invoices", new CreateInvoiceRequest("", "", "USD"), ct);
        badRequest.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await badRequest.Content.ReadAsStringAsync(ct)).ShouldContain("\"errors\"");

        HttpResponseMessage conflict = await client.PostAsync($"/api/v1/invoices/{id}/pay", content: null, ct);
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}
