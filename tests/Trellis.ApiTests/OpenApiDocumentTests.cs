using System.Text.Json;
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
public sealed class OpenApiDocumentTests
{
    private const string ProblemMediaType = "application/problem+json";
    private const string ProblemSchemaRef = "#/components/schemas/ProblemResponse";
    private static readonly string[] HttpMethods = ["get", "post", "put", "patch", "delete"];

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

    [Fact]
    public void Paging_parameters_are_camel_case_with_bounds()
    {
        JsonElement list = Operation("get", "/api/v1/invoices/overdue");
        Dictionary<string, JsonElement> parameters = Parameters(list).ToDictionary(static p => p.GetProperty("name").GetString()!, static p => p.GetProperty("schema"), StringComparer.Ordinal);

        parameters.Keys.ShouldBe(["page", "pageSize"], StringComparer.Ordinal, ignoreOrder: true);
        parameters["page"].GetProperty("minimum").GetInt32().ShouldBe(1);
        parameters["page"].GetProperty("maximum").GetInt32().ShouldBe(10_000);
        parameters["page"].GetProperty("default").GetInt32().ShouldBe(1);
        parameters["pageSize"].GetProperty("minimum").GetInt32().ShouldBe(1);
        parameters["pageSize"].GetProperty("maximum").GetInt32().ShouldBe(100);
        parameters["pageSize"].GetProperty("default").GetInt32().ShouldBe(20);
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

        return [.. document.RootElement.GetProperty("paths").EnumerateObject()
            .Select(static path => (path.Name, path.Value.Clone()))];
    }

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
