using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Shouldly;
using Trellis.Api.Contracts.Auth;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;
using Trellis.ServiceDefaults;
using Xunit;

namespace Trellis.ApiTests;

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

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Trellis_AppHost>(ct);
        UseThrowawayDatabase(appHost);
        await using DistributedApplication app = await appHost.BuildAsync(ct);
        await app.StartAsync(ct);

        // Healthy implies the migration worker already completed (api WaitsForCompletion on it).
        await app.ResourceNotifications.WaitForResourceHealthyAsync(ServiceNames.Api, ct);
        using HttpClient client = app.CreateHttpClient(ServiceNames.Api);

        // Invoice endpoints require permissions — anonymous access is rejected.
        (await client.GetAsync("/api/v1/invoices/overdue", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Authenticate as the seeded admin (invoices:read + invoices:write).
        HttpResponseMessage loginResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest("admin@example.com", "Admin123!"), ct);
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        TokenResponse tokens = (await loginResponse.Content.ReadFromJsonAsync<TokenResponse>(ct))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        // The overdue listing, against the Development seed (MigrationWorker.SeedSampleDataAsync): it holds
        // one invoice already past due — the only way to have one, since Issue rejects a past due date.
        // A page of one exercises the paged envelope; whatever else is listed is not this test's concern.
        PagedResponse<InvoiceDto> firstPage = (await client.GetFromJsonAsync<PagedResponse<InvoiceDto>>(
            "/api/v1/invoices/overdue?page=1&pageSize=1", ct))!;
        firstPage.Page.ShouldBe(1);
        firstPage.PageSize.ShouldBe(1);
        firstPage.TotalCount.ShouldBeGreaterThanOrEqualTo(1);
        firstPage.TotalPages.ShouldBe(firstPage.TotalCount);
        firstPage.HasPreviousPage.ShouldBeFalse();
        firstPage.HasNextPage.ShouldBe(firstPage.TotalCount > 1);
        InvoiceDto oldestOverdue = firstPage.Items.ShouldHaveSingleItem();
        oldestOverdue.Status.ShouldBe(InvoiceStatus.Issued);
        oldestOverdue.DueDate.ShouldNotBeNull().ShouldBeLessThan(DateTimeOffset.UtcNow);

        PagedResponse<InvoiceDto> overdueBefore = (await client.GetFromJsonAsync<PagedResponse<InvoiceDto>>(
            "/api/v1/invoices/overdue?pageSize=100", ct))!;
        overdueBefore.Items.ShouldContain(static i => i.CustomerName == "Overdue Industries" && i.Total == 1200.00m);
        overdueBefore.Items.Select(static i => i.DueDate).ShouldBeInOrder(SortDirection.Ascending);

        // create draft
        Guid id = await CreateDraftAsync(client, "ACME Corp", ct);

        // add lines
        (await client.PostAsJsonAsync($"/api/v1/invoices/{id}/lines", new AddInvoiceLineRequest("Design", 150.50m), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync($"/api/v1/invoices/{id}/lines", new AddInvoiceLineRequest("Build", 349.50m), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // issue, due in 30 days (whole days: the date round-trips through Postgres exactly)
        DateTimeOffset dueDate = new(DateTime.UtcNow.Date.AddDays(30), TimeSpan.Zero);
        (await client.PostAsJsonAsync($"/api/v1/invoices/{id}/issue", new IssueInvoiceRequest(dueDate), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // read back the full aggregate
        InvoiceDto invoice = (await client.GetFromJsonAsync<InvoiceDto>($"/api/v1/invoices/{id}", ct))!;
        invoice.Status.ShouldBe(InvoiceStatus.Issued);
        invoice.DueDate.ShouldBe(dueDate);
        invoice.Total.ShouldBe(500.00m);
        invoice.Lines.Count.ShouldBe(2);

        // not yet due, so not overdue: the listing is exactly what it was before this invoice was issued
        PagedResponse<InvoiceDto> overdueAfter = (await client.GetFromJsonAsync<PagedResponse<InvoiceDto>>(
            "/api/v1/invoices/overdue?pageSize=100", ct))!;
        overdueAfter.Items.ShouldNotContain(i => i.Id == id);
        overdueAfter.TotalCount.ShouldBe(overdueBefore.TotalCount);

        // pay
        (await client.PostAsync($"/api/v1/invoices/{id}/pay", content: null, ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // due-date rule: a past date is the aggregate's 400, a missing one the validator's 400
        Guid second = await CreateDraftAsync(client, "Late Ltd", ct);
        (await client.PostAsJsonAsync($"/api/v1/invoices/{second}/lines", new AddInvoiceLineRequest("Audit", 10m), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HttpResponseMessage pastDue = await client.PostAsJsonAsync(
            $"/api/v1/invoices/{second}/issue", new IssueInvoiceRequest(DateTimeOffset.UtcNow.AddDays(-1)), ct);
        pastDue.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await pastDue.Content.ReadAsStringAsync(ct)).ShouldContain("Invoice.DueDateInPast");

        using StringContent noDueDate = new("{}", Encoding.UTF8, "application/json");
        HttpResponseMessage missingDue = await client.PostAsync($"/api/v1/invoices/{second}/issue", noDueDate, ct);
        missingDue.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await missingDue.Content.ReadAsStringAsync(ct)).ShouldContain("\"errors\"");

        // void is terminal: the rejected issues left a draft, which voids once and only once
        (await client.PostAsync($"/api/v1/invoices/{second}/void", content: null, ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsync($"/api/v1/invoices/{second}/void", content: null, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // error contracts
        HttpResponseMessage notFound = await client.GetAsync($"/api/v1/invoices/{Guid.NewGuid()}", ct);
        notFound.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        HttpResponseMessage badRequest = await client.PostWithIdempotencyKeyAsync(
            "/api/v1/invoices", new CreateInvoiceRequest("", "", "USD"), ct);
        badRequest.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await badRequest.Content.ReadAsStringAsync(ct)).ShouldContain("\"errors\"");

        HttpResponseMessage conflict = await client.PostAsync($"/api/v1/invoices/{id}/pay", content: null, ct);
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    private static async Task<Guid> CreateDraftAsync(HttpClient client, string customerName, CancellationToken ct)
    {
        HttpResponseMessage createResponse = await client.PostWithIdempotencyKeyAsync(
            "/api/v1/invoices",
            new CreateInvoiceRequest(customerName, "billing@acme.com", "USD"),
            ct);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        createResponse.Headers.Location.ShouldNotBeNull();
        return (await createResponse.Content.ReadFromJsonAsync<CreateInvoiceResponse>(ct))!.Id;
    }

    /// <summary>
    /// The AppHost gives postgres a named data volume so <c>aspire run</c> keeps its data between runs.
    /// The volume name derives from the AppHost project, so without this the test would open, and write
    /// to, the developer's <c>aspire run</c> database. Without the volume mount every run starts from an
    /// empty database that the migration worker creates and seeds from scratch.
    /// </summary>
    private static void UseThrowawayDatabase(IDistributedApplicationTestingBuilder appHost)
    {
        IResource postgres = appHost.Resources.Single(static r => r.Name == ServiceNames.Postgres);
        List<ContainerMountAnnotation> volumes = [.. postgres.Annotations
            .OfType<ContainerMountAnnotation>()
            .Where(static m => m.Type == ContainerMountType.Volume)];
        volumes.ShouldNotBeEmpty("the AppHost no longer mounts a data volume on postgres; drop UseThrowawayDatabase");

        foreach (ContainerMountAnnotation volume in volumes)
        {
            postgres.Annotations.Remove(volume);
        }
    }
}
