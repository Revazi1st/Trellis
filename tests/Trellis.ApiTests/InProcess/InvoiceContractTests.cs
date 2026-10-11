using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// What the OpenAPI document promises about the invoice endpoints, proven on the wire (ADR-0025): the
/// conditional GET honours every <c>If-None-Match</c> form RFC 9110 allows (not only the exact strong
/// string the server emits), a write honours an optional <c>If-Match</c> and answers a stale one with a
/// 412 that changes nothing, the paging bounds the document states are the bounds the API enforces
/// (naming the field), and a 201's <c>Location</c> resolves to the created invoice.
/// </summary>
public sealed class InvoiceContractTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("\"{0}\"")]                   // the strong form the server emits
    [InlineData("W/\"{0}\"")]                 // the weak form: If-None-Match compares weakly
    [InlineData("*")]                         // any current representation
    [InlineData("W/\"0\", W/\"{0}\"")]        // a list: any member may match
    [InlineData("\"stale\", \"{0}\"")]        // a list of strong tags
    public async Task Get_invoice_answers_304_for_every_matching_If_None_Match_form(string format)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        (Guid id, long version) = await CreateAndReadAsync(admin, ct);

        using HttpResponseMessage response = await GetWithIfNoneMatchAsync(admin, id, string.Format(System.Globalization.CultureInfo.InvariantCulture, format, version), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        response.Headers.ETag.ShouldNotBeNull().ToString().ShouldBe($"\"{version}\"");
        (await response.Content.ReadAsByteArrayAsync(ct)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("W/\"0\"")]
    [InlineData("\"0\"")]
    [InlineData("not-an-etag")]
    public async Task Get_invoice_answers_200_with_the_body_for_a_stale_or_unparsable_tag(string ifNoneMatch)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        (Guid id, long version) = await CreateAndReadAsync(admin, ct);

        using HttpResponseMessage response = await GetWithIfNoneMatchAsync(admin, id, ifNoneMatch, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldNotBeNull().ToString().ShouldBe($"\"{version}\"");
        (await response.Content.ReadFromJsonAsync<InvoiceDto>(ct)).ShouldNotBeNull().Id.ShouldBe(id);
    }

    [Fact]
    public async Task A_write_naming_the_current_ETag_applies_and_a_stale_one_is_a_412_that_changes_nothing()
    {
        // The exposure If-Match closes: a colleague adds a line after you loaded the draft, and your Issue
        // would issue lines and a total you never saw.
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        (Guid id, long loaded) = await CreateAndReadAsync(admin, ct);

        (await PostWithIfMatchAsync(admin, $"/api/v1/invoices/{id}/lines", new AddInvoiceLineRequest("Design", 10m), $"\"{loaded}\"", ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HttpResponseMessage stale = await PostWithIfMatchAsync(
            admin, $"/api/v1/invoices/{id}/issue", new IssueInvoiceRequest(DateTimeOffset.UtcNow.AddDays(7)), $"\"{loaded}\"", ct);

        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        ProblemResponse problem = (await stale.Content.ReadFromJsonAsync<ProblemResponse>(ct)).ShouldNotBeNull();
        problem.Code.ShouldBe("Invoice.VersionMismatch");
        InvoiceDto unchanged = (await admin.GetFromJsonAsync<InvoiceDto>($"/api/v1/invoices/{id}", ct)).ShouldNotBeNull();
        unchanged.Status.ShouldBe(InvoiceStatus.Draft, "a failed precondition changes nothing");
        unchanged.Version.ShouldNotBe(loaded, "adding the line moved the version a child write included");

        (await PostWithIfMatchAsync(admin, $"/api/v1/invoices/{id}/issue", new IssueInvoiceRequest(DateTimeOffset.UtcNow.AddDays(7)), $"\"{unchanged.Version}\"", ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await PostWithIfMatchAsync<object>(admin, $"/api/v1/invoices/{id}/pay", body: null, "*", ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent, "* matches any current representation");
    }

    [Theory]
    [InlineData("W/\"{0}\"")]                 // a weak tag: If-Match compares strongly
    [InlineData("\"{0}\", \"0\"")]           // a list: send the one tag you read
    [InlineData("\"abc\"")]                   // not a tag this API issued
    [InlineData("not-an-etag")]               // unparsable
    public async Task An_If_Match_that_names_no_single_current_version_is_a_412(string format)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        (Guid id, long version) = await CreateAndReadAsync(admin, ct);
        string ifMatch = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, version);

        HttpResponseMessage response = await PostWithIfMatchAsync<object>(admin, $"/api/v1/invoices/{id}/void", body: null, ifMatch, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await admin.GetFromJsonAsync<InvoiceDto>($"/api/v1/invoices/{id}", ct)).ShouldNotBeNull().Status.ShouldBe(InvoiceStatus.Draft);
    }

    [Fact]
    public async Task A_missing_invoice_is_a_404_whatever_If_Match_names()
    {
        // RFC 9110 §13.2.1: a precondition is ignored when the answer without it would not be a 2xx.
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        HttpResponseMessage response = await PostWithIfMatchAsync<object>(admin, $"/api/v1/invoices/{Guid.NewGuid()}/void", body: null, "\"7\"", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("page=0", "page")]
    [InlineData("page=10001", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    public async Task Paging_outside_the_documented_bounds_is_a_400_naming_the_field(string query, string field)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        HttpResponseMessage response = await admin.GetAsync($"/api/v1/invoices/overdue?{query}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ProblemResponse problem = JsonSerializer.Deserialize<ProblemResponse>(await response.Content.ReadAsStringAsync(ct), JsonSerializerOptions.Web).ShouldNotBeNull();
        problem.Code.ShouldBe("Validation.General");
        problem.Errors.ShouldNotBeNull().ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            error => error.Field.ShouldBe(field),
            error => error.Code.ShouldBe("Validation.InclusiveBetween"));
    }

    [Fact]
    public async Task Paging_within_the_bounds_binds_the_camel_case_names()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        PagedResponse<InvoiceDto> page = (await admin.GetFromJsonAsync<PagedResponse<InvoiceDto>>(
            $"/api/v1/invoices/overdue?page={PagingLimits.MaxPage}&pageSize={PagingLimits.MaxPageSize}", ct)).ShouldNotBeNull();

        page.Page.ShouldBe(PagingLimits.MaxPage);
        page.PageSize.ShouldBe(PagingLimits.MaxPageSize);
        page.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_answers_201_whose_Location_resolves_to_the_invoice()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        HttpResponseMessage created = await admin.PostWithIdempotencyKeyAsync(
            "/api/v1/invoices", new CreateInvoiceRequest("Location Ltd", "l@l.co", "USD"), ct);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid id = (await created.Content.ReadFromJsonAsync<CreateInvoiceResponse>(ct)).ShouldNotBeNull().Id;
        Uri location = created.Headers.Location.ShouldNotBeNull();
        location.PathAndQuery.ShouldBe($"/api/v1/invoices/{id}");
        (await admin.GetFromJsonAsync<InvoiceDto>(location, ct)).ShouldNotBeNull().Id.ShouldBe(id);
    }

    private static async Task<(Guid Id, long Version)> CreateAndReadAsync(HttpClient client, CancellationToken ct)
    {
        HttpResponseMessage created = await client.PostWithIdempotencyKeyAsync(
            "/api/v1/invoices", new CreateInvoiceRequest("Conditional Co", "c@c.co", "USD"), ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid id = (await created.Content.ReadFromJsonAsync<CreateInvoiceResponse>(ct)).ShouldNotBeNull().Id;

        InvoiceDto invoice = (await client.GetFromJsonAsync<InvoiceDto>($"/api/v1/invoices/{id}", ct)).ShouldNotBeNull();
        return (id, invoice.Version);
    }

    private static async Task<HttpResponseMessage> PostWithIfMatchAsync<TBody>(HttpClient client, string path, TBody? body, string ifMatch, CancellationToken ct)
        where TBody : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch).ShouldBeTrue();
        return await client.SendAsync(request, ct);
    }

    private static async Task<HttpResponseMessage> GetWithIfNoneMatchAsync(HttpClient client, Guid id, string ifNoneMatch, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/invoices/{id}");
        request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return await client.SendAsync(request, ct);
    }
}
