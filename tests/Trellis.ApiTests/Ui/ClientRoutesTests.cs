#if (IncludeUI)
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Trellis.Api.Contracts.Auth;
using Trellis.Api.Contracts.Invoices;
using Trellis.UI.Common.Services;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// The UI's typed clients write their routes by hand (<c>$"api/v1/invoices/{id}/pay"</c>), and the API
/// declares its own in the endpoints: nothing compiles the two together, so a route renamed on one side
/// still builds and fails only in a browser. This holds each request a client sends to the committed
/// contract, <c>docs/api/openapi.v1.json</c> (which <c>OpenApiSnapshotTests</c> proves is the document the
/// API serves): its method and path are an operation of the document, and every query parameter it sends
/// is one that operation declares, in the same spelling.
/// <para>
/// The requests are the real ones: each member of each client is called on <see cref="UiHarness"/> and the
/// request it sent is read back, so a route held in a constant, a method chosen in a helper and a query
/// string built in a branch are all seen. A member the table below does not call fails the second fact,
/// and so does a new client. What it does not see: a branch of a member no row takes (give it a row, as
/// the invoice list has two), the headers and the body, and the base address, which the host sets
/// (<c>AddSharedUi</c>'s caller). An operation no client calls is not a failure: the UI need not use the
/// whole API.
/// </para>
/// </summary>
public sealed partial class ClientRoutesTests
{
    /// <summary>The id every row passes: the one path segment that stands for a route parameter.</summary>
    private static readonly Guid Id = Guid.Parse("0198c5d2-7a31-7c4e-9b1f-3d5a6e7f8091");

    /// <summary>
    /// One row per request a client member can send, keyed by the member. A member with a branch that
    /// changes the request has a row per branch. The answers are dropped: the request is the subject,
    /// and the scripted API answers 404 to every one.
    /// </summary>
    private static readonly ClientCall[] Calls =
    [
        new(typeof(IInvoiceApiClient), nameof(IInvoiceApiClient.ListAsync), async (ui, ct) => _ = await ui.InvoiceApi.ListAsync(cancellationToken: ct)),
        new(typeof(IInvoiceApiClient), nameof(IInvoiceApiClient.ListAsync), async (ui, ct) => _ = await ui.InvoiceApi.ListAsync(InvoiceStatus.Issued, cancellationToken: ct)),
        new(typeof(IInvoiceApiClient), nameof(IInvoiceApiClient.GetOverdueAsync), async (ui, ct) => _ = await ui.InvoiceApi.GetOverdueAsync(cancellationToken: ct)),
        new(typeof(IInvoiceApiClient), nameof(IInvoiceApiClient.GetAsync), async (ui, ct) => _ = await ui.InvoiceApi.GetAsync(Id, ct)),
        new(typeof(IInvoiceApiClient), nameof(IInvoiceApiClient.CreateAsync), async (ui, ct) => _ = await ui.InvoiceApi.CreateAsync(new CreateInvoiceRequest("Acme", "billing@acme.com", "USD"), ct)),
        new(typeof(IInvoiceApiClient), nameof(IInvoiceApiClient.AddLineAsync), async (ui, ct) => _ = await ui.InvoiceApi.AddLineAsync(Id, new AddInvoiceLineRequest("Consulting", 100m), cancellationToken: ct)),
        new(typeof(IInvoiceApiClient), nameof(IInvoiceApiClient.IssueAsync), async (ui, ct) => _ = await ui.InvoiceApi.IssueAsync(Id, new IssueInvoiceRequest(UiHarness.Start.AddDays(14)), cancellationToken: ct)),
        new(typeof(IInvoiceApiClient), nameof(IInvoiceApiClient.PayAsync), async (ui, ct) => _ = await ui.InvoiceApi.PayAsync(Id, cancellationToken: ct)),
        new(typeof(IInvoiceApiClient), nameof(IInvoiceApiClient.VoidAsync), async (ui, ct) => _ = await ui.InvoiceApi.VoidAsync(Id, cancellationToken: ct)),
        new(typeof(IAuthApiClient), nameof(IAuthApiClient.LoginAsync), async (ui, ct) => _ = await ui.AuthApi.LoginAsync(new LoginRequest("admin@example.com", "a-password"), ct)),
        new(typeof(IAuthApiClient), nameof(IAuthApiClient.RefreshAsync), async (ui, ct) => _ = await ui.AuthApi.RefreshAsync(new RefreshRequest("a-refresh-token"), ct)),
        new(typeof(IAuthApiClient), nameof(IAuthApiClient.RegisterAsync), async (ui, ct) => _ = await ui.AuthApi.RegisterAsync(new RegisterRequest("new@example.com", "a-password"), ct)),
        new(typeof(IAuthApiClient), nameof(IAuthApiClient.LogoutAsync), async (ui, ct) => _ = await ui.AuthApi.LogoutAsync(new LogoutRequest("a-refresh-token"), ct)),
    ];

    [Fact]
    public async Task Every_request_a_client_sends_is_an_operation_of_the_committed_contract()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        List<SentRequest> sent = [];
        foreach (ClientCall call in Calls)
        {
            await using var ui = new UiHarness();
            await call.Send(ui, ct);

            RecordedRequest request = ui.Api.Requests.ShouldHaveSingleItem($"{call.Name} sends one request");
            sent.Add(SentRequest.From(call.Name, request, Id));
        }

        using JsonDocument contract = JsonDocument.Parse(await File.ReadAllTextAsync(OpenApiDocumentTests.CommittedDocumentPath(), ct));

        sent.ShouldNotBeEmpty();
        Mismatches(sent, contract.RootElement.GetProperty("paths")).ShouldBeEmpty(
            "a typed client and the API disagree about a route: correct the client (src/Presentation/UI/…/Services), or, when the "
            + "endpoint changed on purpose, regenerate docs/api/openapi.v1.json (UPDATE_OPENAPI_SNAPSHOT=1) and then correct the client");
    }

    [Fact]
    public void Every_member_of_every_typed_client_is_called_by_the_table()
    {
        Type[] clients = TypedClientInterfaces(typeof(IAuthApiClient).Assembly);

        clients.ShouldContain(typeof(IAuthApiClient), "the reader no longer finds the typed clients of UI.Common");
        Mislabelled(Calls).ShouldBeEmpty("a row of Calls names one member and calls another: name the member the row's call invokes");
        Uncalled(clients, Calls.Select(static call => call.Name)).ShouldBeEmpty(
            "a typed client's member sends a request no row of Calls checks against the contract: add a row that calls it");
    }

    /// <summary>
    /// Positive control for the route check: a path the document does not hold (a literal segment misspelt,
    /// which a parameter of the neighbouring route must not swallow), a method it does not declare on that
    /// path and a query parameter in another spelling are reported; the request the document describes is not.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/v1/things/{}", "page,pageSize", null)]
    [InlineData("GET", "/api/v1/things/overdue", "", null)]
    [InlineData("GET", "/api/v1/things/overdu", "", "GET /api/v1/things/overdu is not an operation")]
    [InlineData("GET", "/api/v1/thing/{}", "", "GET /api/v1/thing/{} is not an operation")]
    [InlineData("PUT", "/api/v1/things/{}", "", "PUT /api/v1/things/{} is not an operation")]
    [InlineData("GET", "/api/v1/things/{}", "page,pagesize", "the query parameter 'pagesize'")]
    public void The_route_check_flags_a_path_a_method_and_a_query_parameter_the_contract_does_not_declare(
        string method, string shape, string queryNames, string? reported)
    {
        using JsonDocument planted = JsonDocument.Parse(
            """
            {
              "/api/v1/things/overdue": { "get": {} },
              "/api/v1/things/{thingId}": {
                "summary": "A path item holds more than operations",
                "parameters": [ { "name": "thingId", "in": "path" } ],
                "get": { "parameters": [
                  { "name": "thingId", "in": "path" },
                  { "name": "page", "in": "query" },
                  { "name": "pageSize", "in": "query" },
                  { "name": "pagesize", "in": "header" } ] },
                "post": {}
              }
            }
            """);
        SentRequest request = new("IThingApiClient.GetAsync", method, shape, [.. queryNames.Split(',', StringSplitOptions.RemoveEmptyEntries)]);

        string[] mismatches = Mismatches([request], planted.RootElement);

        if (reported is null)
        {
            mismatches.ShouldBeEmpty();
        }
        else
        {
            mismatches.ShouldHaveSingleItem().ShouldContain(reported);
        }
    }

    /// <summary>
    /// Positive control for the table check: a member with no row is reported, by its client and its name, and
    /// so is a row that names one member and calls another, which would otherwise stand in for the member it names.
    /// </summary>
    [Fact]
    public void The_table_check_flags_a_member_no_row_calls_and_a_row_that_calls_another_member()
    {
        Uncalled([typeof(IAuthApiClient)], ["IAuthApiClient.LoginAsync", "IAuthApiClient.RefreshAsync", "IAuthApiClient.RegisterAsync"])
            .ShouldBe(["IAuthApiClient.LogoutAsync"]);

        ClientCall[] rows =
        [
            new(typeof(IAuthApiClient), nameof(IAuthApiClient.LoginAsync), async (ui, ct) => _ = await ui.AuthApi.LoginAsync(new LoginRequest("admin@example.com", "a-password"), ct)),
            new(typeof(IAuthApiClient), nameof(IAuthApiClient.LogoutAsync), async (ui, ct) => _ = await ui.AuthApi.LoginAsync(new LoginRequest("admin@example.com", "a-password"), ct)),
        ];
        Mislabelled(rows).ShouldBe(["IAuthApiClient.LogoutAsync"]);
    }

    /// <summary>
    /// The interfaces of the UI's typed clients: every interface of UI.Common that a class taking an
    /// <see cref="HttpClient"/> implements. Found by what a client is, not by its name, so a new one is
    /// held to the table without an edit here.
    /// </summary>
    private static Type[] TypedClientInterfaces(Assembly ui)
        => [.. ui.GetTypes()
            .Where(static type => type is { IsClass: true, IsAbstract: false }
                && type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(static constructor => constructor.GetParameters().Any(static parameter => parameter.ParameterType == typeof(HttpClient))))
            .SelectMany(static type => type.GetInterfaces())
            .Where(type => type.Assembly == ui)
            .Distinct()
            .OrderBy(static type => type.Name, StringComparer.Ordinal)];

    /// <summary>The members of <paramref name="clients"/> that <paramref name="called"/> does not name, as <c>IClient.Member</c>.</summary>
    private static string[] Uncalled(IEnumerable<Type> clients, IEnumerable<string> called)
    {
        HashSet<string> names = [.. called];
        return [.. clients
            .SelectMany(static client => client.GetMethods().Select(method => $"{client.Name}.{method.Name}"))
            .Distinct(StringComparer.Ordinal)
            .Where(name => !names.Contains(name))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>The rows whose call, as it is written, does not invoke the member the row names.</summary>
    private static string[] Mislabelled(IEnumerable<ClientCall> calls)
        => [.. calls.Where(static call => !call.Written.Contains($".{call.Member}(", StringComparison.Ordinal)).Select(static call => call.Name)];

    /// <summary>
    /// What is wrong with each request against the contract's <c>paths</c>: its method and path shape are no
    /// operation, or it sends a query parameter the operation does not declare.
    /// </summary>
    private static string[] Mismatches(IEnumerable<SentRequest> sent, JsonElement paths)
    {
        Dictionary<string, string[]> operations = new(StringComparer.Ordinal);
        foreach (JsonProperty path in paths.EnumerateObject())
        {
            // A path item also holds keys that are no operation (parameters, summary, description).
            foreach (JsonProperty operation in path.Value.EnumerateObject()
                         .Where(static member => OpenApiDocumentTests.HttpMethods.Contains(member.Name, StringComparer.Ordinal)))
            {
                string[] query = operation.Value.TryGetProperty("parameters", out JsonElement parameters)
                    ? [.. parameters.EnumerateArray()
                        .Where(static parameter => parameter.GetProperty("in").GetString() == "query")
                        .Select(static parameter => parameter.GetProperty("name").GetString()!)]
                    : [];
                operations[$"{operation.Name.ToUpperInvariant()} {RouteParameter().Replace(path.Name, "{}")}"] = query;
            }
        }

        List<string> mismatches = [];
        foreach (SentRequest request in sent)
        {
            string operation = $"{request.Method} {request.Shape}";
            if (!operations.TryGetValue(operation, out string[]? declared))
            {
                mismatches.Add($"{request.Member} sends {operation}, and {operation} is not an operation of the contract");
                continue;
            }

            mismatches.AddRange(request.QueryNames
                .Where(name => !declared.Contains(name, StringComparer.Ordinal))
                .Select(name => $"{request.Member} sends the query parameter '{name}', which {operation} does not declare (it declares: {string.Join(", ", declared)})"));
        }

        return [.. mismatches];
    }

    [GeneratedRegex(@"\{[^{}/]*\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RouteParameter();

    /// <summary>
    /// A member of a typed client and the call that makes it send its request. <paramref name="Written"/> is
    /// the call's own source text, which the compiler supplies, so the table check can hold the name to it.
    /// </summary>
    private sealed record ClientCall(
        Type Client, string Member, Func<UiHarness, CancellationToken, Task> Send, [CallerArgumentExpression(nameof(Send))] string Written = "")
    {
        public string Name => $"{Client.Name}.{Member}";
    }

    /// <summary>
    /// A request as the check reads it: the method, the path with the row's id written as <c>{}</c> (the
    /// document's own templates are read the same way), and the names of its query parameters.
    /// </summary>
    private sealed record SentRequest(string Member, string Method, string Shape, IReadOnlyList<string> QueryNames)
    {
        public static SentRequest From(string member, RecordedRequest request, Guid id)
            => new(
                member,
                request.Method,
                string.Join('/', request.Path.Split('/').Select(segment => string.Equals(segment, id.ToString(), StringComparison.OrdinalIgnoreCase) ? "{}" : segment)),
                [.. request.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(static pair => pair.Split('=')[0])]);
    }
}
#endif
