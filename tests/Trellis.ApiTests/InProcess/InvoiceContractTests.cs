using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;
using Trellis.Application.Authorization;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// What the OpenAPI document promises about the invoice endpoints, proven on the wire (ADR-0025): the
/// conditional GET honours every <c>If-None-Match</c> form RFC 9110 allows (not only the exact strong
/// string the server emits), a write honours an optional <c>If-Match</c> and answers a stale one with a
/// 412 that changes nothing, the paging bounds the document states are the bounds the API enforces
/// (naming the field), a 201's <c>Location</c> resolves to the created invoice, and every invoice is in
/// <c>GET /invoices</c> from the moment it is created, newest first, narrowed by an optional status.
/// </summary>
public sealed class InvoiceContractTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string InvoiceList = "/api/v1/invoices";
    private const string OverdueList = "/api/v1/invoices/overdue";

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
    public async Task Get_invoice_lets_a_browser_keep_and_revalidate_it_while_other_reads_stay_no_store()
    {
        // The conditional GET opts out of the API's no-store default, on the 200 and the 304 alike; a read
        // without an ETag keeps the default (ADR-0021 amendment).
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        (Guid id, long version) = await CreateAndReadAsync(admin, ct);

        using HttpResponseMessage full = await admin.GetAsync($"/api/v1/invoices/{id}", ct);
        using HttpResponseMessage revalidated = await GetWithIfNoneMatchAsync(admin, id, $"\"{version}\"", ct);
        using HttpResponseMessage list = await admin.GetAsync(OverdueList, ct);
        using HttpResponseMessage everyInvoice = await admin.GetAsync(InvoiceList, ct);

        full.StatusCode.ShouldBe(HttpStatusCode.OK);
        revalidated.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        foreach (HttpResponseMessage response in new[] { full, revalidated })
        {
            System.Net.Http.Headers.CacheControlHeaderValue cacheControl = response.Headers.CacheControl.ShouldNotBeNull();
            cacheControl.Private.ShouldBeTrue();
            cacheControl.NoCache.ShouldBeTrue();
            cacheControl.NoStore.ShouldBeFalse();
        }

        foreach (HttpResponseMessage response in new[] { list, everyInvoice })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        }
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
    [InlineData("\"0{0}\"")]                  // the current version with a leading zero: a tag is its exact text
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

    [Theory]
    [InlineData("\"7\"", HttpStatusCode.NotFound)]             // well-formed: the handler looks the invoice up first
    [InlineData("\"07\"", HttpStatusCode.PreconditionFailed)]  // a leading zero: never issued, refused before the lookup
    [InlineData("W/\"7\"", HttpStatusCode.PreconditionFailed)] // weak: can never match, refused before the lookup
    public async Task A_missing_invoice_is_a_404_for_a_well_formed_If_Match_and_a_malformed_one_is_a_412_before_the_lookup(
        string ifMatch, HttpStatusCode expected)
    {
        // RFC 9110 §13.2.1 ignores a precondition when the answer without it would not be a 2xx, so one strong
        // tag waits for the lookup. A header that can never match is refused by the endpoint before the handler
        // runs, and the 412 is the same whether the invoice exists or not.
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        HttpResponseMessage response = await PostWithIfMatchAsync<object>(admin, $"/api/v1/invoices/{Guid.NewGuid()}/void", body: null, ifMatch, ct);

        response.StatusCode.ShouldBe(expected);
    }

    [Theory]
    [InlineData(OverdueList, "page=0", "page")]
    [InlineData(OverdueList, "page=10001", "page")]
    [InlineData(OverdueList, "pageSize=0", "pageSize")]
    [InlineData(OverdueList, "pageSize=101", "pageSize")]
    [InlineData(InvoiceList, "page=0", "page")]
    [InlineData(InvoiceList, "page=10001", "page")]
    [InlineData(InvoiceList, "pageSize=0", "pageSize")]
    [InlineData(InvoiceList, "pageSize=101", "pageSize")]
    [InlineData(InvoiceList, "status=Draft&pageSize=101", "pageSize")]
    public async Task Paging_outside_the_documented_bounds_is_a_400_naming_the_field(string path, string query, string field)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        HttpResponseMessage response = await admin.GetAsync($"{path}?{query}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ProblemResponse problem = JsonSerializer.Deserialize<ProblemResponse>(await response.Content.ReadAsStringAsync(ct), JsonSerializerOptions.Web).ShouldNotBeNull();
        problem.Code.ShouldBe("Validation.General");
        problem.Errors.ShouldNotBeNull().ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            error => error.Field.ShouldBe(field),
            error => error.Code.ShouldBe("Validation.InclusiveBetween"));
    }

    [Theory]
    [InlineData(OverdueList)]
    [InlineData(InvoiceList)]
    public async Task Paging_within_the_bounds_binds_the_camel_case_names(string path)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        PagedResponse<InvoiceDto> page = (await admin.GetFromJsonAsync<PagedResponse<InvoiceDto>>(
            $"{path}?page={PagingLimits.MaxPage}&pageSize={PagingLimits.MaxPageSize}", ct)).ShouldNotBeNull();

        page.Page.ShouldBe(PagingLimits.MaxPage);
        page.PageSize.ShouldBe(PagingLimits.MaxPageSize);
        page.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_created_then_issued_invoice_heads_the_list_for_every_reader_and_is_not_overdue()
    {
        // The reported gap: an invoice created and issued (due in the future, as Issue demands) appeared in no
        // list, for the administrator who made it or anyone else. It is the newest invoice, so it heads the list.
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        HttpClient reader = factory.CreateClientWithPermissions(Permissions.Invoices.Read);
        string customer = $"Listed {Guid.NewGuid():N}";
        Guid id = await admin.CreateInvoiceAsync(customer);
        await admin.AddInvoiceLineAsync(id, "Listing", 250m);
        await admin.IssueInvoiceAsync(id, dueDate: factory.Time.GetUtcNow().AddDays(30));

        foreach (HttpClient client in new[] { admin, reader })
        {
            using HttpResponseMessage response = await client.GetAsync(InvoiceList, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.ShouldHaveMediaType("application/json");
            PagedResponse<InvoiceDto> page = (await response.Content.ReadFromJsonAsync<PagedResponse<InvoiceDto>>(ct)).ShouldNotBeNull();
            InvoiceDto first = page.Items[0];
            first.Id.ShouldBe(id);
            first.CustomerName.ShouldBe(customer);
            first.Status.ShouldBe(InvoiceStatus.Issued);
            first.Total.ShouldBe(250m);
            page.Page.ShouldBe(PagingLimits.MinPage);
            page.PageSize.ShouldBe(PagingLimits.DefaultPageSize);
            page.Items.Select(static invoice => invoice.CreatedAtUtc).ShouldBeInOrder(SortDirection.Descending);
        }

        PagedResponse<InvoiceDto> overdue = (await admin.GetFromJsonAsync<PagedResponse<InvoiceDto>>(
            $"{OverdueList}?pageSize={PagingLimits.MaxPageSize}", ct)).ShouldNotBeNull();
        overdue.Items.ShouldNotContain(invoice => invoice.Id == id, "it is due in 30 days");
    }

    [Fact]
    public async Task The_list_is_the_paged_envelope_in_camel_case_with_the_status_by_name()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        Guid id = await admin.CreateInvoiceAsync($"Envelope {Guid.NewGuid():N}");

        using JsonDocument body = JsonDocument.Parse(await admin.GetStringAsync($"{InvoiceList}?pageSize=1", ct));

        JsonElement root = body.RootElement;
        root.EnumerateObject().Select(static member => member.Name).ShouldBe(
            ["items", "page", "pageSize", "totalCount", "totalPages", "hasNextPage", "hasPreviousPage"], ignoreOrder: true);
        JsonElement invoice = root.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        invoice.GetProperty("id").GetGuid().ShouldBe(id);
        invoice.GetProperty("status").GetString().ShouldBe("Draft");
        invoice.GetProperty("createdAtUtc").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UnixEpoch);
        root.GetProperty("pageSize").GetInt32().ShouldBe(1);
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.Issued)]
    public async Task A_status_narrows_the_list_to_the_invoices_in_that_state(InvoiceStatus status)
    {
        // Created last of their state, each heads its own filtered list.
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        Guid issued = await admin.CreateInvoiceAsync($"Issued {Guid.NewGuid():N}");
        await admin.AddInvoiceLineAsync(issued);
        await admin.IssueInvoiceAsync(issued, dueDate: factory.Time.GetUtcNow().AddDays(30));
        Guid draft = await admin.CreateInvoiceAsync($"Draft {Guid.NewGuid():N}");
        (Guid expected, Guid other) = status == InvoiceStatus.Draft ? (draft, issued) : (issued, draft);

        PagedResponse<InvoiceDto> page = (await admin.GetFromJsonAsync<PagedResponse<InvoiceDto>>(
            $"{InvoiceList}?status={status}&pageSize={PagingLimits.MaxPageSize}", ct)).ShouldNotBeNull();

        page.Items.ShouldAllBe(invoice => invoice.Status == status);
        page.Items[0].Id.ShouldBe(expected);
        page.Items.ShouldNotContain(invoice => invoice.Id == other);
    }

    [Fact]
    public async Task A_status_name_outside_the_enum_is_a_400_naming_the_parameter()
    {
        // The binder refuses a name it cannot parse ("Overdue" is a list of its own, not a status); its
        // message, the problem's detail, names the parameter.
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        HttpResponseMessage response = await admin.GetAsync($"{InvoiceList}?status=Overdue", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.ShouldBeProblemJson();
        ProblemResponse problem = JsonSerializer.Deserialize<ProblemResponse>(await response.Content.ReadAsStringAsync(ct), JsonSerializerOptions.Web).ShouldNotBeNull();
        problem.Code.ShouldBe("Request.Malformed");
        problem.Detail.ShouldNotBeNull().ShouldContain("status");
    }

    [Fact]
    public async Task An_ordinal_the_status_enum_does_not_define_is_a_400_naming_the_field()
    {
        // An ordinal parses (Enum.TryParse accepts "99"), so the query's validator is what refuses it.
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        HttpResponseMessage response = await admin.GetAsync($"{InvoiceList}?status=99", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ProblemResponse problem = JsonSerializer.Deserialize<ProblemResponse>(await response.Content.ReadAsStringAsync(ct), JsonSerializerOptions.Web).ShouldNotBeNull();
        problem.Code.ShouldBe("Validation.General");
        problem.Errors.ShouldNotBeNull().ShouldHaveSingleItem().Field.ShouldBe("status");
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
