using System.Text.Json;
using Shouldly;
using Trellis.Api.Contracts.Invoices;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// Fast guards on the public wire contracts — they run everywhere (no orchestration needed),
/// so the ApiTests assembly always executes at least one test even when the E2E flow is skipped.
/// </summary>
public sealed class ContractSerializationTests
{
    // The API host uses ASP.NET Core's default (camelCase) JSON; clients must round-trip against the same policy.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void InvoiceDto_roundtrips_with_camel_case_property_names_and_a_named_status()
    {
        var dto = new InvoiceDto(
            Guid.CreateVersion7(),
            "ACME Corp",
            "billing@acme.com",
            "USD",
            InvoiceStatus.Issued,
            new DateTimeOffset(2026, 7, 15, 9, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            null,
            500.00m,
            7L,
            [new InvoiceLineDto(Guid.CreateVersion7(), "Design", 150.50m)]);

        string json = JsonSerializer.Serialize(dto, Options);

        json.ShouldContain("\"customerName\":");
        json.ShouldContain("\"lines\":");
        // The status travels by name, never by ordinal: that is what the document's string enum promises.
        json.ShouldContain("\"status\":\"Issued\"");
        json.ShouldContain("\"version\":7");
        json.ShouldContain("\"createdAtUtc\":\"2026-07-15T09:30:00+00:00\"");

        // Record equality is reference-based over the Lines collection, so compare the shape explicitly.
        InvoiceDto? roundTripped = JsonSerializer.Deserialize<InvoiceDto>(json, Options);
        roundTripped.ShouldNotBeNull();
        roundTripped.ShouldBe(dto with { Lines = roundTripped.Lines });
        roundTripped.Lines.ShouldBe(dto.Lines);
    }

    [Fact]
    public void CreateInvoiceRequest_deserializes_from_a_client_payload()
    {
        const string payload = """{"customerName":"ACME","customerEmail":"a@b.co","currency":"USD"}""";

        CreateInvoiceRequest? request = JsonSerializer.Deserialize<CreateInvoiceRequest>(payload, Options);

        request.ShouldNotBeNull();
        request.CustomerName.ShouldBe("ACME");
        request.CustomerEmail.ShouldBe("a@b.co");
        request.Currency.ShouldBe("USD");
    }
}
