extern alias apihost;

using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// Quality gates on the committed OpenAPI contract (<c>docs/api/openapi.v1.json</c>, ADR-0013, ADR-0025).
/// <para>
/// <c>OpenApiSnapshotTests</c> proves the committed file is the document the API serves; these prove
/// the document's CONTENT is one a consumer can generate a client from: concrete paths, an
/// <c>operationId</c> per operation, the problem schema on every error, the headers and statuses the
/// endpoints really answer with, bounded paging and typed money/status/version. A new endpoint that
/// ships without prose, or a transformer that stops doing its job, fails here with the operation named.
/// </para>
/// </summary>
public sealed partial class OpenApiDocumentTests
{
    private const string ProblemMediaType = "application/problem+json";
    private const string ProblemSchemaRef = "#/components/schemas/ProblemResponse";
    /// <summary>The keys of a path item that are operations (it may also hold <c>parameters</c>, <c>summary</c> and <c>description</c>).</summary>
    internal static readonly string[] HttpMethods = ["get", "post", "put", "patch", "delete"];

    /// <summary>
    /// The writes under a resource whose GET answers an <c>ETag</c> that take no <c>If-Match</c>, each
    /// ("POST /api/v1/…") with the reason it cannot: a write that creates the record it names, say, has no
    /// version to name yet. Empty while every such write is conditional.
    /// </summary>
    private static readonly Dictionary<string, string> WritesWithoutIfMatch = new(StringComparer.Ordinal);

    /// <summary>
    /// The writes to a record whose GET answers no <c>ETag</c>, each named with its record
    /// ("GET /api/v1/…/{id} &lt;- DELETE /api/v1/…/{id}") and the reason the record hands out no version for
    /// it, the sentence its feature doc gives: a tag is deleted, never edited, say. A row excuses the one
    /// write it names, so a write added to the record later fails until the record is versioned or that write
    /// has a row of its own. Empty while every such record is versioned.
    /// </summary>
    private static readonly Dictionary<string, string> WritesToRecordsWithoutVersion = new(StringComparer.Ordinal);

    [Fact]
    public void Every_operation_carries_a_summary_and_a_description()
    {
        List<string> undocumented = [];

        foreach ((string name, JsonElement operation) in AllOperations())
        {
            if (!Has(operation, "summary") || !Has(operation, "description"))
            {
                undocumented.Add(name);
            }
        }

        undocumented.ShouldBeEmpty(
            "every endpoint needs .WithSummary() and .WithDescription(); then regenerate "
            + $"docs/api/openapi.v1.json (UPDATE_OPENAPI_SNAPSHOT=1). Missing: {string.Join(", ", undocumented)}");
    }

    [Fact]
    public void Health_endpoints_stay_out_of_the_public_contract()
    {
        // /health and /alive are infrastructure probes mapped outside the versioned API group. If
        // they ever appear here it means they were accidentally described (and, with a document-level
        // security requirement, would be advertised as authenticated routes they are not).
        IReadOnlyList<string> paths = [.. Operations().Select(static entry => entry.Path)];

        paths.ShouldNotContain(static path => string.Equals(path, "/health", StringComparison.Ordinal));
        paths.ShouldNotContain(static path => string.Equals(path, "/alive", StringComparison.Ordinal));
    }

    [Fact]
    public void The_document_is_the_v1_contract_and_is_not_empty()
    {
        using JsonDocument document = ReadDocument();

        document.RootElement.GetProperty("info").GetProperty("version").GetString().ShouldBe("1.0.0");
        Operations().ShouldNotBeEmpty();
    }

    [Fact]
    public void Every_operation_has_a_unique_operationId()
    {
        // MapEndpoints names every route after its class; the name is the operationId a generated client uses as the method name.
        List<(string Name, string? OperationId)> ids = [.. AllOperations()
            .Select(static entry => (entry.Name, entry.Operation.TryGetProperty("operationId", out JsonElement id) ? id.GetString() : null))];

        ids.ShouldAllBe(static entry => !string.IsNullOrWhiteSpace(entry.OperationId), string.Join(", ", ids.Where(static i => i.OperationId is null).Select(static i => i.Name)));
        ids.Select(static entry => entry.OperationId).ShouldBeUnique(StringComparer.Ordinal);
    }

    /// <summary>
    /// A client generator and the API reference group operations by their tag, so every operation carries
    /// one, and it is a tag a feature declares: the <c>Tag</c> constant of its <c>&lt;Feature&gt;Endpoints</c>
    /// class (<c>AuthEndpoints.Tag</c>), which each endpoint passes to <c>.WithTags(...)</c>. Counting the
    /// tags would not catch a forgotten call: ASP.NET Core then tags the operation with the endpoint class's
    /// name, one tag again and a group of one in every client. So the value is held, to the constants the
    /// Api assembly declares, read by reflection without starting the host.
    /// <para>
    /// The document is the subject, not the endpoint's metadata: a tag a route group adds is overridden by the
    /// endpoint's own and never reaches the document, so it is not seen here.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_operation_carries_one_tag_and_a_feature_declares_it()
    {
        string[] declared = DeclaredTags(typeof(apihost::Program).Assembly);

        declared.ShouldNotBeEmpty("the reader no longer finds a 'const string Tag' in the Api assembly (AuthEndpoints.Tag)");
        OperationsOffTheirTag(Operations(), declared).ShouldBeEmpty(
            $"every endpoint calls .WithTags(<Feature>Endpoints.Tag) with its feature's Tag constant (declared: {string.Join(", ", declared)}); "
            + "an endpoint without the call is tagged with its class name, and a literal is a group of its own. "
            + "Then regenerate docs/api/openapi.v1.json (UPDATE_OPENAPI_SNAPSHOT=1)");
    }

    /// <summary>
    /// Positive control: an operation with no tags, with an empty list, with a blank tag, with two, and with one
    /// tag no feature declares (the endpoint class's name, which is what a forgotten <c>.WithTags</c> leaves, and
    /// a declared tag in another case) is reported; one that carries a declared tag is not.
    /// </summary>
    [Fact]
    public void The_tag_check_flags_no_tag_two_tags_and_a_tag_no_feature_declares()
    {
        using JsonDocument planted = JsonDocument.Parse(
            """
            {
              "paths": {
                "/api/v1/things": { "get": { "tags": ["Things"] }, "post": {} },
                "/api/v1/things/{id}": {
                  "parameters": [],
                  "get": { "tags": ["Things", "Admin"] },
                  "put": { "tags": [" "] },
                  "patch": { "tags": ["RenameThing"] },
                  "delete": { "tags": [] }
                },
                "/api/v1/things/{id}/archive": { "post": { "tags": ["things"] } }
              }
            }
            """);

        string[] reported =
        [
            "DELETE /api/v1/things/{id} (no tag)",
            "GET /api/v1/things/{id} (two tags: Things, Admin)",
            "PATCH /api/v1/things/{id} (tagged RenameThing, which no Tag constant declares)",
            "POST /api/v1/things (no tag)",
            "POST /api/v1/things/{id}/archive (tagged things, which no Tag constant declares)",
            "PUT /api/v1/things/{id} (a blank tag)",
        ];
        OperationsOffTheirTag(PathsOf(planted), ["Admin", "Things"]).ShouldBe(reported);
    }

    /// <summary>The value of every <c>const string Tag</c> a type of <paramref name="api"/> declares, in order.</summary>
    private static string[] DeclaredTags(Assembly api)
        => [.. api.GetTypes()
            .SelectMany(static type => type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(static field => field is { IsLiteral: true, Name: "Tag" } && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Each operation that does not carry exactly one tag out of <paramref name="declared"/>, as
    /// <c>METHOD path (what it carries)</c>, in order.
    /// </summary>
    private static string[] OperationsOffTheirTag(IEnumerable<(string Path, JsonElement Item)> paths, IReadOnlyCollection<string> declared)
        => [.. paths
            .SelectMany(static entry => entry.Item.EnumerateObject()
                .Where(static o => HttpMethods.Contains(o.Name, StringComparer.Ordinal))
                .Select(o => (Name: $"{o.Name.ToUpperInvariant()} {entry.Path}", Tags: TagsOf(o.Value))))
            .Select(operation => (operation.Name, Problem: operation.Tags switch
            {
                [] => "no tag",
                [var tag] when string.IsNullOrWhiteSpace(tag) => "a blank tag",
                [var tag] when !declared.Contains(tag, StringComparer.Ordinal) => $"tagged {tag}, which no Tag constant declares",
                [_] => null,
                _ => $"two tags: {string.Join(", ", operation.Tags)}",
            }))
            .Where(static operation => operation.Problem is not null)
            .Select(static operation => $"{operation.Name} ({operation.Problem})")
            .Order(StringComparer.Ordinal)];

    private static string[] TagsOf(JsonElement operation)
        => operation.TryGetProperty("tags", out JsonElement tags) ? [.. tags.EnumerateArray().Select(static tag => tag.GetString() ?? string.Empty)] : [];

    [Fact]
    public void Every_path_template_variable_is_a_declared_path_parameter()
    {
        // A consumer must be able to fill every {token} in; {version} used to leak from the route group with no parameter behind it.
        foreach ((string path, JsonElement item) in Operations())
        {
            List<string> tokens = PathTokens(path);
            path.ShouldNotContain("{version}");

            foreach (JsonProperty operation in item.EnumerateObject().Where(static o => HttpMethods.Contains(o.Name, StringComparer.Ordinal)))
            {
                string[] declared = [.. Parameters(operation.Value)
                    .Where(static p => p.GetProperty("in").GetString() == "path")
                    .Select(static p => p.GetProperty("name").GetString()!)];

                declared.ShouldBe(tokens, StringComparer.Ordinal, ignoreOrder: true, $"{operation.Name.ToUpperInvariant()} {path}");
            }
        }
    }

    [Fact]
    public void Document_declares_no_servers()
    {
        using JsonDocument document = ReadDocument();

        // The generator fills servers from whatever address served the request; a committed contract must not pin a host.
        document.RootElement.TryGetProperty("servers", out _).ShouldBeFalse();
    }

    [Fact]
    public void Every_4xx_and_5xx_response_uses_the_problem_schema()
    {
        using JsonDocument document = ReadDocument();
        JsonElement schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        // The component is generated from Api.Contracts.Common.ProblemResponse, the record the UI deserialises into (ADR-0024).
        JsonElement problem = schemas.GetProperty("ProblemResponse").GetProperty("properties");
        foreach (string member in new[] { "type", "title", "status", "detail", "instance", "code", "correlationId", "traceId", "errors" })
        {
            problem.TryGetProperty(member, out _).ShouldBeTrue($"ProblemResponse.{member}");
        }

        // Only what every problem carries is required: `errors` is absent outside a validation 400, `detail` on a status-code-pages 404.
        string[] required = [.. schemas.GetProperty("ProblemResponse").GetProperty("required").EnumerateArray().Select(static r => r.GetString()!)];
        required.ShouldBe(["type", "title", "status", "code"], StringComparer.Ordinal, ignoreOrder: true);

        problem.GetProperty("errors").GetProperty("items").GetProperty("$ref").GetString().ShouldBe("#/components/schemas/FieldError");
        JsonElement fieldError = schemas.GetProperty("FieldError").GetProperty("properties");
        fieldError.EnumerateObject().Select(static p => p.Name).ShouldBe(["field", "code", "description"], StringComparer.Ordinal, ignoreOrder: true);

        foreach ((string name, JsonElement operation) in AllOperations())
        {
            foreach (JsonProperty response in operation.GetProperty("responses").EnumerateObject())
            {
                if (response.Name[0] is not ('4' or '5'))
                {
                    continue;
                }

                string reference = response.Value.GetProperty("content").GetProperty(ProblemMediaType)
                    .GetProperty("schema").GetProperty("$ref").GetString()!;
                reference.ShouldBe(ProblemSchemaRef, $"{name} {response.Name}");
            }
        }
    }

    [Fact]
    public void Operations_with_input_document_400_and_id_routes_document_404()
    {
        foreach ((string name, JsonElement operation) in AllOperations())
        {
            IReadOnlyList<string> statuses = [.. operation.GetProperty("responses").EnumerateObject().Select(static r => r.Name)];
            bool hasBody = operation.TryGetProperty("requestBody", out _);
            bool hasRouteId = Parameters(operation).Any(static p => p.GetProperty("in").GetString() == "path");
            bool hasQuery = Parameters(operation).Any(static p => p.GetProperty("in").GetString() == "query");

            statuses.Contains("400", StringComparer.Ordinal).ShouldBe(hasBody || hasRouteId || hasQuery, $"{name}: 400 iff there is input to reject");
            statuses.Contains("404", StringComparer.Ordinal).ShouldBe(hasRouteId, $"{name}: 404 iff a route id can miss");
            statuses.Contains("429", StringComparer.Ordinal).ShouldBeTrue($"{name}: every operation is rate limited");
            statuses.Contains("500", StringComparer.Ordinal).ShouldBeTrue($"{name}: the opaque 500 is part of the contract");
        }
    }

    [Theory]
    [InlineData("/api/v1/auth/login", new[] { "401", "403" })]
    [InlineData("/api/v1/auth/register", new[] { "403", "409" })]
    [InlineData("/api/v1/auth/refresh", new[] { "401" })]
    public void Anonymous_auth_operations_declare_their_result_statuses(string path, string[] statuses)
    {
        // No security requirement and no route id: the shape cannot infer these, so the endpoint declares them with a bare
        // .Produces(status) and the transformer gives each the problem body (ADR-0025). Undeclared, a generated client maps the
        // commonest failure of the sign-in call to a generic exception instead of ProblemResponse.
        JsonElement responses = Operation("post", path).GetProperty("responses");

        foreach (string status in statuses)
        {
            responses.TryGetProperty(status, out JsonElement response).ShouldBeTrue($"POST {path} answers {status} through CustomResults.Problem");
            response.GetProperty("content").GetProperty(ProblemMediaType).GetProperty("schema").GetProperty("$ref").GetString()
                .ShouldBe(ProblemSchemaRef, $"POST {path} {status}");
        }
    }

    [Fact]
    public void Created_responses_document_Location()
    {
        JsonElement create = Operation("post", "/api/v1/invoices");

        JsonElement created = create.GetProperty("responses").GetProperty("201");
        created.GetProperty("headers").GetProperty("Location").GetProperty("schema").GetProperty("format").GetString().ShouldBe("uri");
    }

    [Fact]
    public void Conditional_get_documents_If_None_Match_ETag_and_304()
    {
        JsonElement get = Operation("get", "/api/v1/invoices/{id}");

        Parameters(get).ShouldContain(static p => p.GetProperty("name").GetString() == "If-None-Match" && p.GetProperty("in").GetString() == "header");
        JsonElement responses = get.GetProperty("responses");
        responses.GetProperty("200").GetProperty("headers").TryGetProperty("ETag", out _).ShouldBeTrue();
        responses.TryGetProperty("304", out JsonElement notModified).ShouldBeTrue();
        notModified.TryGetProperty("content", out _).ShouldBeFalse("a 304 has no body");
    }

    [Theory]
    [InlineData("/api/v1/invoices/{id}/lines")]
    [InlineData("/api/v1/invoices/{id}/issue")]
    [InlineData("/api/v1/invoices/{id}/pay")]
    [InlineData("/api/v1/invoices/{id}/void")]
    public void Conditional_writes_document_an_optional_If_Match_and_412(string path)
    {
        JsonElement write = Operation("post", path);

        JsonElement ifMatch = Parameters(write).Where(static p => p.GetProperty("name").GetString() == "If-Match").ShouldHaveSingleItem();
        ifMatch.GetProperty("in").GetString().ShouldBe("header");
        (ifMatch.TryGetProperty("required", out JsonElement required) && required.GetBoolean()).ShouldBeFalse("If-Match is optional: no 428");
        write.GetProperty("responses").GetProperty("412").GetProperty("content").GetProperty(ProblemMediaType)
            .GetProperty("schema").GetProperty("$ref").GetString().ShouldBe(ProblemSchemaRef);
    }

    /// <summary>
    /// A resource that hands out its version takes it back on every write: under a path whose GET answers an
    /// <c>ETag</c>, each other operation on that path or below it declares <c>If-Match</c>
    /// (<c>.SupportsConditionalWrite()</c>), so two callers changing one record cannot overwrite each other
    /// unseen. A write that cannot take one is in <see cref="WritesWithoutIfMatch"/> with its reason.
    /// </summary>
    [Fact]
    public void Every_write_to_a_resource_that_answers_an_ETag_takes_If_Match()
    {
        PreconditionAudit audit = AuditPreconditions(Operations(), WritesWithoutIfMatch);

        audit.Unguarded.ShouldBeEmpty(
            "a write to a resource whose GET answers an ETag takes If-Match: .SupportsConditionalWrite(), the command's ExpectedVersion and a 412 on a stale one, as the invoice writes do; or a row in WritesWithoutIfMatch with the reason it cannot");
        audit.Stale.ShouldBeEmpty("a row in WritesWithoutIfMatch that names no unconditional write under a versioned resource");
    }

    [Fact]
    public void The_If_Match_check_flags_an_unconditional_write_under_a_versioned_resource()
    {
        // A versioned resource (its GET answers an ETag) with a conditional write, unconditional ones (on the
        // path itself and below it, two of them naming its parameter differently: each endpoint declares its own
        // route) and an exempt one; a create on the collection above it, an action on a literal segment beside
        // the resource, and a resource that hands out no version, none of which can name one.
        using JsonDocument planted = JsonDocument.Parse(
            """
            {
              "paths": {
                "/api/v1/things": { "post": { "parameters": [] } },
                "/api/v1/things/export": { "post": {} },
                "/api/v1/things/{id}": {
                  "get": { "responses": { "200": { "headers": { "ETag": { "schema": { "type": "string" } } } } } },
                  "delete": { "parameters": [ { "name": "id", "in": "path" } ] }
                },
                "/api/v1/things/{id}/rename": { "post": { "parameters": [ { "name": "If-Match", "in": "header" } ] } },
                "/api/v1/things/{id}/archive": { "post": { "parameters": [ { "name": "id", "in": "path" } ] } },
                "/api/v1/things/{thingId}/retire": { "post": { "parameters": [ { "name": "thingId", "in": "path" } ] } },
                "/api/v1/things/{thingId}/lines/{lineId}": { "delete": {} },
                "/api/v1/things/{id}/notes": { "post": {} },
                "/api/v1/notes/{id}": { "get": { "responses": { "200": {} } } },
                "/api/v1/notes/{id}/pin": { "post": {} }
              }
            }
            """);

        PreconditionAudit audit = AuditPreconditions(
            PathsOf(planted),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["POST /api/v1/things/{id}/notes"] = "a note is a record of its own",
                ["POST /api/v1/things/{id}/gone"] = "a write that no longer exists",
            });

        string[] unguarded =
        [
            "DELETE /api/v1/things/{id}",
            "DELETE /api/v1/things/{thingId}/lines/{lineId}",
            "POST /api/v1/things/{id}/archive",
            "POST /api/v1/things/{thingId}/retire",
        ];
        string[] stale = ["POST /api/v1/things/{id}/gone"];
        audit.Unguarded.ShouldBe(unguarded);
        audit.Stale.ShouldBe(stale);
    }

    /// <summary>
    /// A record that is written to after its create hands out its version: the GET of a path ending in a route
    /// parameter, with a write on that path or below it, answers an <c>ETag</c>
    /// (<c>.SupportsConditionalGet()</c>), and the fact above then asks each of those writes to take it back.
    /// Without one, two callers who change the record overwrite each other unseen. A write to a record that
    /// needs none is in <see cref="WritesToRecordsWithoutVersion"/> with its reason, each write on a row of
    /// its own.
    /// </summary>
    [Fact]
    public void Every_record_with_a_write_under_it_answers_an_ETag()
    {
        VersionAudit audit = AuditVersions(Operations(), WritesToRecordsWithoutVersion);

        audit.Unversioned.ShouldBeEmpty(
            "a record with a write under it hands out its version: Version on the DTO and a strong ETag on its GET (.SupportsConditionalGet(), as GetInvoice does), then If-Match on each write; or a row in WritesToRecordsWithoutVersion for each write listed here, keyed as it is listed, with the reason the record needs no version for it, the sentence its feature doc gives");
        audit.Stale.ShouldBeEmpty("a row in WritesToRecordsWithoutVersion that names no write to a record whose GET answers no ETag");
    }

    [Fact]
    public void The_version_check_flags_a_record_with_a_write_under_it_and_no_ETag()
    {
        // Flagged, each write named with its record: a record whose GET answers no ETag with a write below it
        // (naming its parameter differently), one with a write on its own path that a row naming the record
        // alone does not excuse, and one with an exempt write and two that are not, on its path and below it.
        // Not flagged: a versioned record; a record below it, which the root's version guards; a record
        // nothing writes to after the create on the collection above it, its path-level parameters being no
        // operation; a list and a literal segment, which are no record; and a record with a write on it and
        // below it and no GET, which has nothing to answer an ETag on.
        using JsonDocument planted = JsonDocument.Parse(
            """
            {
              "paths": {
                "/api/v1/things": { "get": { "responses": { "200": {} } }, "post": {} },
                "/api/v1/things/export": { "get": { "responses": { "200": {} } }, "post": {} },
                "/api/v1/things/{id}": {
                  "get": { "responses": { "200": { "headers": { "ETag": { "schema": { "type": "string" } } } } } },
                  "delete": {}
                },
                "/api/v1/things/{thingId}/lines/{lineId}": { "get": { "responses": { "200": {} } }, "delete": {} },
                "/api/v1/notes/{id}": { "get": { "responses": { "200": {} } } },
                "/api/v1/notes/{noteId}/pin": { "post": {} },
                "/api/v1/tags/{id}": { "get": { "responses": { "200": {} } }, "delete": {} },
                "/api/v1/reports": { "post": {} },
                "/api/v1/reports/{id}": {
                  "parameters": [ { "name": "id", "in": "path" } ],
                  "get": { "responses": { "200": {} } }
                },
                "/api/v1/jobs/{id}": { "delete": {} },
                "/api/v1/jobs/{id}/cancel": { "post": {} },
                "/api/v1/labels/{id}": { "get": { "responses": { "200": {} } }, "put": {}, "delete": {} },
                "/api/v1/labels/{labelId}/merge": { "post": {} }
              }
            }
            """);

        VersionAudit audit = AuditVersions(
            PathsOf(planted),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GET /api/v1/labels/{id} <- DELETE /api/v1/labels/{id}"] = "a deleted label has nothing left to overwrite",
                ["GET /api/v1/labels/{id} <- PATCH /api/v1/labels/{id}"] = "a write that no longer exists",
                ["GET /api/v1/reports/{id} <- POST /api/v1/reports"] = "a create above the record, which is no write to it",
                ["GET /api/v1/tags/{id}"] = "a row that names the record and none of its writes",
            });

        string[] unversioned =
        [
            "GET /api/v1/labels/{id} <- POST /api/v1/labels/{labelId}/merge",
            "GET /api/v1/labels/{id} <- PUT /api/v1/labels/{id}",
            "GET /api/v1/notes/{id} <- POST /api/v1/notes/{noteId}/pin",
            "GET /api/v1/tags/{id} <- DELETE /api/v1/tags/{id}",
        ];
        string[] stale =
        [
            "GET /api/v1/labels/{id} <- PATCH /api/v1/labels/{id}",
            "GET /api/v1/reports/{id} <- POST /api/v1/reports",
            "GET /api/v1/tags/{id}",
        ];
        audit.Unversioned.ShouldBe(unversioned);
        audit.Stale.ShouldBe(stale);
    }

    [Theory]
    [InlineData("/api/v1/invoices/overdue", new[] { "page", "pageSize" })]
    [InlineData("/api/v1/invoices", new[] { "status", "page", "pageSize" })]
    public void Paging_parameters_are_camel_case_with_bounds(string path, string[] names)
    {
        JsonElement list = Operation("get", path);
        Dictionary<string, JsonElement> parameters = Parameters(list).ToDictionary(static p => p.GetProperty("name").GetString()!, static p => p.GetProperty("schema"), StringComparer.Ordinal);

        parameters.Keys.ShouldBe(names, StringComparer.Ordinal, ignoreOrder: true);
        parameters["page"].GetProperty("minimum").GetInt32().ShouldBe(1);
        parameters["page"].GetProperty("maximum").GetInt32().ShouldBe(10_000);
        parameters["page"].GetProperty("default").GetInt32().ShouldBe(1);
        parameters["pageSize"].GetProperty("minimum").GetInt32().ShouldBe(1);
        parameters["pageSize"].GetProperty("maximum").GetInt32().ShouldBe(100);
        parameters["pageSize"].GetProperty("default").GetInt32().ShouldBe(20);
    }

    [Fact]
    public void The_list_filter_is_an_optional_status_typed_as_the_string_enum()
    {
        JsonElement status = Parameters(Operation("get", "/api/v1/invoices"))
            .Where(static p => p.GetProperty("name").GetString() == "status").ShouldHaveSingleItem();

        status.GetProperty("in").GetString().ShouldBe("query");
        (status.TryGetProperty("required", out JsonElement required) && required.GetBoolean()).ShouldBeFalse("no status lists every invoice");
        ReferencesInvoiceStatus(status.GetProperty("schema")).ShouldBeTrue(status.GetProperty("schema").GetRawText());
    }

    [Fact]
    public void Invoice_status_is_a_string_enum_money_is_decimal_and_version_is_int64()
    {
        using JsonDocument document = ReadDocument();
        JsonElement schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        JsonElement invoice = schemas.GetProperty("InvoiceDto").GetProperty("properties");

        invoice.GetProperty("status").GetProperty("$ref").GetString().ShouldBe("#/components/schemas/InvoiceStatus");
        JsonElement status = schemas.GetProperty("InvoiceStatus");
        status.GetProperty("type").GetString().ShouldBe("string");
        status.GetProperty("enum").EnumerateArray().Select(static e => e.GetString()).ShouldBe(["Draft", "Issued", "Paid", "Voided"], StringComparer.Ordinal);

        invoice.GetProperty("total").GetProperty("format").GetString().ShouldBe("decimal");
        schemas.GetProperty("InvoiceLineDto").GetProperty("properties").GetProperty("amount").GetProperty("format").GetString().ShouldBe("decimal");
        schemas.GetProperty("AddInvoiceLineRequest").GetProperty("properties").GetProperty("amount").GetProperty("format").GetString().ShouldBe("decimal");
        invoice.GetProperty("version").GetProperty("format").GetString().ShouldBe("int64");
    }

    /// <summary>The schema is the InvoiceStatus component, directly or as the non-null member of a nullable union.</summary>
    private static bool ReferencesInvoiceStatus(JsonElement schema)
    {
        const string StatusRef = "#/components/schemas/InvoiceStatus";
        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            return reference.GetString() == StatusRef;
        }

        foreach (string union in new[] { "oneOf", "anyOf", "allOf" })
        {
            if (schema.TryGetProperty(union, out JsonElement members)
                && members.EnumerateArray().Any(ReferencesInvoiceStatus))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every write ("POST /api/v1/…") on a path whose GET answers an <c>ETag</c>, or below one, that declares no
    /// <c>If-Match</c> header and is not exempt, and every exempt name that is no such write, each sorted. Paths
    /// are compared with each route parameter blanked (<see cref="RouteShape"/>), since every endpoint declares
    /// its own template and a write may name the id differently from its GET; a name keeps the path as written.
    /// </summary>
    private static PreconditionAudit AuditPreconditions(
        IReadOnlyList<(string Path, JsonElement Item)> paths, IReadOnlyDictionary<string, string> exempt)
    {
        string[] versioned = [.. paths
            .Where(static entry => entry.Item.TryGetProperty("get", out JsonElement get) && AnswersETag(get))
            .Select(static entry => RouteShape(entry.Path))];

        SortedSet<string> unguarded = new(StringComparer.Ordinal);
        HashSet<string> excused = new(StringComparer.Ordinal);
        foreach ((string path, JsonElement item) in paths)
        {
            string shape = RouteShape(path);
            if (!versioned.Any(resource => IsAtOrBelow(shape, resource)))
            {
                continue;
            }

            foreach (JsonProperty write in item.EnumerateObject()
                         .Where(static o => o.Name != "get" && HttpMethods.Contains(o.Name, StringComparer.Ordinal)))
            {
                string name = $"{write.Name.ToUpperInvariant()} {path}";
                if (Parameters(write.Value).Any(static p => IsHeader(p, "If-Match")))
                {
                    continue;
                }

                if (exempt.ContainsKey(name))
                {
                    excused.Add(name);
                }
                else
                {
                    unguarded.Add(name);
                }
            }
        }

        return new PreconditionAudit(
            [.. unguarded],
            [.. exempt.Keys.Where(name => !excused.Contains(name)).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Every write to a record whose GET answers no <c>ETag</c>, named with that GET
    /// ("GET /api/v1/…/{id} &lt;- DELETE /api/v1/…/{id}") and not exempt under that name, and every exempt name
    /// that is no such write, each sorted. A record is a path that ends in a route parameter, has a GET and sits
    /// under no record that does answer an <c>ETag</c>; a write to it is any other operation on its path or
    /// below it. Paths are compared as <see cref="AuditPreconditions"/> compares them.
    /// </summary>
    private static VersionAudit AuditVersions(
        IReadOnlyList<(string Path, JsonElement Item)> paths, IReadOnlyDictionary<string, string> exempt)
    {
        string[] versioned = [.. paths
            .Where(static entry => entry.Item.TryGetProperty("get", out JsonElement get) && AnswersETag(get))
            .Select(static entry => RouteShape(entry.Path))];
        (string Name, string Shape)[] writes = [.. paths
            .SelectMany(static entry => entry.Item.EnumerateObject()
                .Where(static o => o.Name != "get" && HttpMethods.Contains(o.Name, StringComparer.Ordinal))
                .Select(o => ($"{o.Name.ToUpperInvariant()} {entry.Path}", RouteShape(entry.Path))))];

        SortedSet<string> unversioned = new(StringComparer.Ordinal);
        HashSet<string> excused = new(StringComparer.Ordinal);
        foreach ((string path, JsonElement item) in paths)
        {
            string shape = RouteShape(path);
            if (!shape.EndsWith("/{}", StringComparison.Ordinal)
                || !item.TryGetProperty("get", out _)
                || versioned.Any(resource => IsAtOrBelow(shape, resource)))
            {
                continue;
            }

            foreach ((string write, _) in writes.Where(candidate => IsAtOrBelow(candidate.Shape, shape)))
            {
                string name = $"GET {path} <- {write}";
                if (exempt.ContainsKey(name))
                {
                    excused.Add(name);
                }
                else
                {
                    unversioned.Add(name);
                }
            }
        }

        return new VersionAudit(
            [.. unversioned],
            [.. exempt.Keys.Where(name => !excused.Contains(name)).Order(StringComparer.Ordinal)]);
    }

    private static bool IsAtOrBelow(string shape, string resource)
        => shape == resource || shape.StartsWith(resource + "/", StringComparison.Ordinal);

    /// <summary>The path with every route parameter (<c>{id}</c>, <c>{invoiceId}</c>) written as <c>{}</c>.</summary>
    private static string RouteShape(string path) => RouteParameter().Replace(path, "{}");

    [GeneratedRegex(@"\{[^{}/]*\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RouteParameter();

    private static bool AnswersETag(JsonElement get)
        => get.TryGetProperty("responses", out JsonElement responses)
            && responses.EnumerateObject().Any(static response =>
                response.Value.TryGetProperty("headers", out JsonElement headers)
                && headers.EnumerateObject().Any(static header => string.Equals(header.Name, "ETag", StringComparison.OrdinalIgnoreCase)));

    private static bool IsHeader(JsonElement parameter, string name)
        => parameter.TryGetProperty("in", out JsonElement location) && location.GetString() == "header"
            && string.Equals(parameter.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase);

    private sealed record PreconditionAudit(IReadOnlyList<string> Unguarded, IReadOnlyList<string> Stale);

    private sealed record VersionAudit(IReadOnlyList<string> Unversioned, IReadOnlyList<string> Stale);

    private static bool Has(JsonElement operation, string property) =>
        operation.TryGetProperty(property, out JsonElement value)
        && !string.IsNullOrWhiteSpace(value.GetString());

    private static JsonElement[] Parameters(JsonElement operation)
        => operation.TryGetProperty("parameters", out JsonElement parameters) ? [.. parameters.EnumerateArray()] : [];

    /// <summary>The <c>{token}</c>s of a path template, in order.</summary>
    private static List<string> PathTokens(string path)
    {
        List<string> tokens = [];
        for (int start = path.IndexOf('{', StringComparison.Ordinal); start >= 0; start = path.IndexOf('{', start + 1))
        {
            int end = path.IndexOf('}', start);
            if (end < 0)
            {
                break;
            }

            tokens.Add(path[(start + 1)..end]);
        }

        return tokens;
    }

    private static JsonElement Operation(string method, string path)
    {
        (string Path, JsonElement Item) entry = Operations().Single(e => string.Equals(e.Path, path, StringComparison.Ordinal));
        return entry.Item.GetProperty(method);
    }

    /// <summary>Every operation as ("POST /api/v1/invoices", operation).</summary>
    private static List<(string Name, JsonElement Operation)> AllOperations()
        => [.. Operations().SelectMany(static entry => entry.Item.EnumerateObject()
            .Where(static o => HttpMethods.Contains(o.Name, StringComparer.Ordinal))
            .Select(o => ($"{o.Name.ToUpperInvariant()} {entry.Path}", o.Value)))];

    private static List<(string Path, JsonElement Item)> Operations()
    {
        using JsonDocument document = ReadDocument();

        return PathsOf(document);
    }

    private static List<(string Path, JsonElement Item)> PathsOf(JsonDocument document)
        => [.. document.RootElement.GetProperty("paths").EnumerateObject()
            .Select(static path => (path.Name, path.Value.Clone()))];

    private static JsonDocument ReadDocument() => JsonDocument.Parse(File.ReadAllText(CommittedDocumentPath()));

    /// <summary>The committed contract, located by walking up from the test output directory (also used by the snapshot test's update mode).</summary>
    internal static string CommittedDocumentPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "docs", "api", "openapi.v1.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("docs/api/openapi.v1.json was not found above the test output directory.");
    }
}
