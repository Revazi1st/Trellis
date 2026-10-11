using Shouldly;
using Trellis.Domain.Common;
using Xunit;

namespace Trellis.Domain.UnitTests.Common;

/// <summary>
/// <see cref="IScalarValueObject{TSelf, TValue}"/>: what a converted column stores is <c>Value</c>, and
/// <c>Restore</c> gives back an equal value object, without re-running the checks the value already passed
/// when it was written.
/// </summary>
public sealed class ScalarValueObjectTests
{
    [Fact]
    public void An_email_address_round_trips_through_its_stored_value()
        => RoundTrip<EmailAddress, string>(EmailAddress.Create("jane@example.com").Value).ShouldBe("jane@example.com");

    [Fact]
    public void A_currency_round_trips_through_its_code()
        => RoundTrip<Currency, string>(Currency.Create("kwd").Value).ShouldBe("KWD");

    [Fact]
    public void Restoring_skips_the_checks_so_a_tightened_rule_never_makes_an_old_row_unreadable()
    {
        // Refused by today's Create (doubled dot), yet a row written before that rule must still load.
        const string storedBeforeTheRule = "user..name@example.com";
        EmailAddress.Create(storedBeforeTheRule).IsFailure.ShouldBeTrue();

        Restore<EmailAddress, string>(storedBeforeTheRule).Value.ShouldBe(storedBeforeTheRule);
    }

    private static TValue RoundTrip<TValueObject, TValue>(TValueObject valueObject)
        where TValueObject : IScalarValueObject<TValueObject, TValue>
        where TValue : notnull
    {
        TValueObject restored = Restore<TValueObject, TValue>(valueObject.Value);
        restored.ShouldBe(valueObject);
        return restored.Value;
    }

    private static TValueObject Restore<TValueObject, TValue>(TValue value)
        where TValueObject : IScalarValueObject<TValueObject, TValue>
        where TValue : notnull
        => TValueObject.Restore(value);
}
