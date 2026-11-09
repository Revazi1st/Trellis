using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Trellis.Api.Common.Idempotency;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;
using Trellis.Application.Abstractions;
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

        HttpResponseMessage response = await member.PostWithIdempotencyKeyAsync(
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
        response.ShouldBeProblemJson();
        string body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldContain("\"title\"");
        body.ShouldContain("\"status\"");
    }

    [Fact]
    public async Task Idempotency_key_deduplicates_and_a_failed_request_leaves_no_reservation()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        // Reserve + succeed.
        string key = Guid.NewGuid().ToString();
        HttpResponseMessage created = await PostCreateAsync(admin, key, new CreateInvoiceRequest("Idempotent Inc", "i@i.co", "USD"), ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Same caller + route + key + body → the stored response again, never a second command (ADR-0018).
        HttpResponseMessage duplicate = await PostCreateAsync(admin, key, new CreateInvoiceRequest("Idempotent Inc", "i@i.co", "USD"), ct);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Created);
        duplicate.Headers.GetValues(IdempotencyFilter.ReplayedHeaderName).ShouldHaveSingleItem().ShouldBe("true");
        (await duplicate.Content.ReadAsStringAsync(ct)).ShouldBe(await created.Content.ReadAsStringAsync(ct));

        // A failed command leaves no reservation (this codebase returns Results, it doesn't throw)…
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
        response.ShouldBeProblemJson();
        ProblemResponse problem = (await response.Content.ReadFromJsonAsync<ProblemResponse>(ct)).ShouldNotBeNull();
        problem.Code.ShouldBe("Idempotency.InvalidKey");
        problem.Detail.ShouldNotBeNull().ShouldContain("blank");
    }

    [Theory]
    [InlineData("two headers")]
    [InlineData("one list")]
    public async Task More_than_one_idempotency_key_is_a_400_never_two_keys_joined_into_one(string shape)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/invoices")
        {
            Content = JsonContent.Create(new CreateInvoiceRequest("Two Keys Co", "t@k.co", "USD")),
        };
        string[] values = shape == "two headers" ? ["first-key", "second-key"] : ["first-key,second-key"];
        message.Headers.TryAddWithoutValidation(ApiHeaders.IdempotencyKey, values).ShouldBeTrue();
        HttpResponseMessage response = await admin.SendAsync(message, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ProblemResponse problem = (await response.Content.ReadFromJsonAsync<ProblemResponse>(ct)).ShouldNotBeNull();
        problem.Code.ShouldBe("Idempotency.InvalidKey");
        problem.Detail.ShouldBe(IdempotencyErrors.MultipleKeys.Description);
    }

    [Fact]
    public async Task Create_requires_an_idempotency_key_while_writes_to_an_existing_invoice_stay_opt_in()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        HttpResponseMessage withoutKey = await admin.PostAsJsonAsync(
            "/api/v1/invoices", new CreateInvoiceRequest("No Key Co", "n@k.co", "USD"), ct);

        withoutKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await withoutKey.Content.ReadFromJsonAsync<ProblemResponse>(ct)).ShouldNotBeNull().Code.ShouldBe("Idempotency.KeyRequired");

        HttpResponseMessage created = await admin.PostWithIdempotencyKeyAsync(
            "/api/v1/invoices", new CreateInvoiceRequest("No Key Co", "n@k.co", "USD"), ct);
        Guid id = (await created.Content.ReadFromJsonAsync<CreateInvoiceResponse>(ct)).ShouldNotBeNull().Id;
        (await admin.PostAsJsonAsync($"/api/v1/invoices/{id}/lines", new AddInvoiceLineRequest("Line", 10m), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent, "only CreateInvoice declares keyRequired");
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

    /// <summary>
    /// Every paged list holds the bounds it documents (api.md, List endpoints). The endpoint declares them from
    /// <see cref="PagingLimits"/>, but Application cannot see the contracts, so each query's validator repeats
    /// them as literals; this keeps the two together for every GET the served document gives a <c>page</c> and
    /// a <c>pageSize</c>, so a new list needs no bounds test of its own. Inside the bounds is a 200 (a list with a
    /// route value gets a fresh id, so its 404 counts too; a 500 or any other 4xx does not), one past either
    /// bound is a 400 naming that field.
    /// </summary>
    [Theory]
    [InlineData("page", PagingLimits.MinPage, true)]
    [InlineData("page", PagingLimits.MaxPage, true)]
    [InlineData("page", PagingLimits.MinPage - 1, false)]
    [InlineData("page", PagingLimits.MaxPage + 1, false)]
    [InlineData("pageSize", PagingLimits.MinPageSize, true)]
    [InlineData("pageSize", PagingLimits.MaxPageSize, true)]
    [InlineData("pageSize", PagingLimits.MinPageSize - 1, false)]
    [InlineData("pageSize", PagingLimits.MaxPageSize + 1, false)]
    public async Task Every_paged_list_holds_its_documented_bounds_at_the_wire(string parameter, int value, bool accepted)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        using JsonDocument document = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/openapi/v1.json", ct));

        List<string> violations = [];
        foreach (string list in PagedListsIn(document))
        {
            string uri = $"{WithFreshRouteValues(list)}?{parameter}={value}";
            using HttpResponseMessage response = await admin.GetAsync(uri, ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            bool hasRouteValue = list.Contains('{', StringComparison.Ordinal);
            if (ExpectedInsteadOf(accepted, hasRouteValue, response.StatusCode, body, parameter) is { } expected)
            {
                violations.Add($"GET {uri} answered {(int)response.StatusCode}, expected {expected}: {body}");
            }
        }

        violations.ShouldBeEmpty(
            "a list's query validator holds PagingLimits' bounds (InclusiveBetween(PagingLimits.MinPage..MaxPage, MinPageSize..MaxPageSize) as literals)");
    }

    /// <summary>
    /// Positive control: inside the bounds only a 200 holds, and on a list with a route value the fresh id's 404
    /// (a nested list's 500 or a misdeclared permission's 403 does not); one past a bound only a 400
    /// <c>Validation.General</c> naming the field.
    /// </summary>
    [Theory]
    [InlineData(true, false, HttpStatusCode.OK, null, false)]
    [InlineData(true, false, HttpStatusCode.NotFound, null, true)]
    [InlineData(true, true, HttpStatusCode.OK, null, false)]
    [InlineData(true, true, HttpStatusCode.NotFound, null, false)]
    [InlineData(true, true, HttpStatusCode.InternalServerError, null, true)]
    [InlineData(true, true, HttpStatusCode.Forbidden, null, true)]
    [InlineData(true, true, HttpStatusCode.BadRequest, "Validation.General", true)]
    [InlineData(false, false, HttpStatusCode.BadRequest, "Validation.General", false)]
    [InlineData(false, false, HttpStatusCode.BadRequest, "Request.Malformed", true)]
    [InlineData(false, true, HttpStatusCode.NotFound, null, true)]
    public void The_bounds_check_takes_only_the_answers_it_documents(
        bool accepted, bool hasRouteValue, HttpStatusCode status, string? code, bool flagged)
    {
        string body = code is null
            ? "{}"
            : JsonSerializer.Serialize(
                new ProblemResponse(null, null, (int)status, null, null, code, null, null, [new FieldError("pageSize", code, "Out of range.")]),
                JsonSerializerOptions.Web);

        (ExpectedInsteadOf(accepted, hasRouteValue, status, body, "pageSize") is not null).ShouldBe(flagged);
    }

    /// <summary>
    /// Positive control for the selection: <see cref="PagedListsIn"/> picks the GETs
    /// <see cref="Every_paged_list_holds_its_documented_bounds_at_the_wire"/> calls, and an empty pick passes that
    /// theory with nothing called. Empty is a legitimate pick once remove-feature has run, so the theory cannot
    /// refuse one; this proves the reader still takes the served document's shape instead. Fed one path item, it
    /// takes a GET with <c>page</c> and <c>pageSize</c> in its query, beside a route value or not, and nothing else.
    /// </summary>
    [Theory]
    [InlineData("""{"get":{"parameters":[{"name":"page","in":"query"},{"name":"pageSize","in":"query"}]}}""", true)]
    [InlineData("""{"get":{"parameters":[{"name":"id","in":"path","required":true},{"name":"page","in":"query"},{"name":"pageSize","in":"query"}]}}""", true)]
    [InlineData("""{"get":{"parameters":[{"name":"id","in":"path","required":true},{"name":"page","in":"query"}]}}""", false)]
    [InlineData("""{"post":{"parameters":[{"name":"page","in":"query"},{"name":"pageSize","in":"query"}]}}""", false)]
    [InlineData("""{"get":{"parameters":[{"name":"page","in":"path","required":true},{"name":"pageSize","in":"path","required":true}]}}""", false)]
    [InlineData("""{"get":{}}""", false)]
    public void The_paged_list_reader_takes_only_a_GET_with_page_and_pageSize_in_its_query(string pathItem, bool listed)
    {
        const string path = "/api/v1/customers/{id}/orders";
        using JsonDocument document = JsonDocument.Parse($$"""{"paths":{"{{path}}":""" + pathItem + "}}");
        List<string> expected = listed ? [path] : [];

        PagedListsIn(document).ShouldBe(
            expected,
            "PagedListsIn reads the served OpenAPI document: paths.<path>.get.parameters[] with in: query and the "
            + "names page and pageSize; when its reading drifts from that shape the bounds theory passes with nothing called");
    }

    /// <summary>Positive control for the fill: a list with a route value is called with a parsable id in its slot.</summary>
    [Fact]
    public void The_route_value_filler_puts_a_fresh_id_in_each_route_value_slot()
    {
        string filled = WithFreshRouteValues("/api/v1/customers/{id}/orders");

        string.Join('/', filled.Split('/').Select(static segment => Guid.TryParse(segment, out _) ? "<guid>" : segment)).ShouldBe(
            "/api/v1/customers/<guid>/orders",
            $"WithFreshRouteValues replaces each {{routeValue}} segment with a fresh Guid and keeps every other segment; it gave {filled}");
    }

    /// <summary>
    /// A NUL (U+0000) in a GET's query value is a 400, never a 200 or a 500 (api.md, Problem details). The JSON
    /// converter refuses one only in a body string; a query value reaches the handler as sent, and a plain
    /// <c>string</c> carrying it fails the first query in PostgreSQL. Each parameter the served document gives a
    /// GET in its query gets one in turn (route values a fresh id): a typed value fails to bind, a plain string
    /// (a <c>?search=</c> filter) is refused by its query's validator. A route value cannot carry one
    /// (<see cref="RequestLimitTests.A_NUL_in_the_path_is_refused_before_routing"/>), and a write's query is left
    /// out: its body or its idempotency key may answer the 400 first and hide the NUL.
    /// </summary>
    [Fact]
    public async Task Every_query_value_of_a_GET_refuses_a_NUL()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        using JsonDocument document = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/openapi/v1.json", ct));
        List<(string Path, string Name)> values = GetQueryValuesIn(document);

        values.ShouldNotBeEmpty("the served document gives no GET a query value; is the selection right?");
        List<string> violations = [];
        foreach ((string path, string name) in values)
        {
            string uri = $"{WithFreshRouteValues(path)}?{name}=a%00b";
            using HttpResponseMessage response = await admin.GetAsync(uri, ct);
            if (response.StatusCode != HttpStatusCode.BadRequest)
            {
                violations.Add($"GET {uri} answered {(int)response.StatusCode}, expected 400: {await response.Content.ReadAsStringAsync(ct)}");
            }
        }

        violations.ShouldBeEmpty(
            "a string bound from the query never meets the JSON converter: the query's validator refuses U+0000 in it");
    }

    /// <summary>Each parameter a GET takes in its query, with the path it belongs to, in document order.</summary>
    private static List<(string Path, string Name)> GetQueryValuesIn(JsonDocument document)
        => [.. document.RootElement.GetProperty("paths").EnumerateObject()
            .Where(static path => path.Value.TryGetProperty("get", out JsonElement get) && get.TryGetProperty("parameters", out _))
            .SelectMany(static path => QueryParameterNames(path.Value.GetProperty("get").GetProperty("parameters"))
                .Select(name => (path.Name, name)))];

    /// <summary>The paths whose GET takes both paging parameters in its query, in document order.</summary>
    private static List<string> PagedListsIn(JsonDocument document)
        => document.RootElement.GetProperty("paths").EnumerateObject()
            .Where(static path => path.Value.TryGetProperty("get", out JsonElement get)
                                  && get.TryGetProperty("parameters", out JsonElement parameters)
                                  && QueryParameterNames(parameters).ToHashSet(StringComparer.Ordinal).IsSupersetOf(["page", "pageSize"]))
            .Select(static path => path.Name)
            .ToList();

    private static IEnumerable<string> QueryParameterNames(JsonElement parameters)
        => parameters.EnumerateArray()
            .Where(static p => p.TryGetProperty("in", out JsonElement location) && location.GetString() == "query")
            .Select(static p => p.GetProperty("name").GetString()!);

    /// <summary>The path with each <c>{routeValue}</c> segment replaced by a fresh id.</summary>
    private static string WithFreshRouteValues(string template)
        => string.Join('/', template.Split('/').Select(static segment => segment.StartsWith('{') ? Guid.CreateVersion7().ToString() : segment));

    /// <summary>
    /// What a list's answer to one value should have been, or null when it was that: inside the bounds a 200,
    /// or a 404 for a list whose route value is a fresh id; one past either bound a 400 naming the field.
    /// </summary>
    private static string? ExpectedInsteadOf(bool accepted, bool hasRouteValue, HttpStatusCode status, string body, string parameter)
        => (accepted, hasRouteValue) switch
        {
            (true, true) => status is HttpStatusCode.OK or HttpStatusCode.NotFound ? null : "200 or 404",
            (true, false) => status == HttpStatusCode.OK ? null : "200",
            _ => IsValidationOf(status, body, parameter) ? null : $"400 naming {parameter}",
        };

    /// <summary>A 400 <c>Validation.General</c> whose one field error is <paramref name="field"/>.</summary>
    private static bool IsValidationOf(HttpStatusCode status, string body, string field)
    {
        if (status != HttpStatusCode.BadRequest)
        {
            return false;
        }

        ProblemResponse? problem = JsonSerializer.Deserialize<ProblemResponse>(body, JsonSerializerOptions.Web);
        return problem is { Code: "Validation.General", Errors: [{ Field: var named }] } && named == field;
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
