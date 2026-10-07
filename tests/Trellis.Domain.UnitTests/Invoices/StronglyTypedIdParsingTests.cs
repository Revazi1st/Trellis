using Shouldly;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Domain;
using Xunit;

namespace Trellis.Domain.UnitTests.Invoices;

/// <summary>
/// <c>IParsable</c> is what binds <c>{id}</c> route values, so Parse/TryParse are the IDs' public input
/// surface: they must round-trip what ToString prints and turn anything else down.
/// </summary>
public sealed class StronglyTypedIdParsingTests
{
    [Fact]
    public void InvoiceId_round_trips_through_ToString_Parse_and_TryParse() => RoundTrips(InvoiceId.New());

    [Fact]
    public void InvoiceLineId_round_trips_through_ToString_Parse_and_TryParse() => RoundTrips(InvoiceLineId.New());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("1234")]
    [InlineData("00000000-0000-0000-0000-00000000000Z")]
    public void TryParse_rejects_text_that_is_not_a_guid(string? text)
    {
        Rejects<InvoiceId>(text);
        Rejects<InvoiceLineId>(text);
    }

    [Fact]
    public void Parse_of_text_that_is_not_a_guid_throws_FormatException()
    {
        // The IParsable contract: Parse throws, TryParse reports. Route binding uses TryParse (→ 400).
        Should.Throw<FormatException>(() => InvoiceId.Parse("not-a-guid", provider: null));
        Should.Throw<FormatException>(() => InvoiceLineId.Parse("not-a-guid", provider: null));
    }

    private static void RoundTrips<TId>(TId id)
        where TId : struct, IStronglyTypedId<TId>, IParsable<TId>
    {
        string text = id.ToString()!;
        text.ShouldBe(id.Value.ToString());

        TId.Parse(text, provider: null).ShouldBe(id);
        TId.TryParse(text, provider: null, out TId parsed).ShouldBeTrue();
        parsed.ShouldBe(id);
        TId.From(id.Value).ShouldBe(id);
    }

    private static void Rejects<TId>(string? text)
        where TId : struct, IStronglyTypedId<TId>, IParsable<TId>
    {
        TId.TryParse(text, provider: null, out TId result).ShouldBeFalse();
        result.Value.ShouldBe(Guid.Empty);
    }
}
