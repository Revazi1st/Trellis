using Shouldly;
using XTemplate.Domain.Common;
using Xunit;

namespace XTemplate.Domain.UnitTests.Common;

public sealed class MoneyTests
{
    [Fact]
    public void Create_normalizes_currency_casing()
    {
        var money = Money.Create(10m, "usd");

        money.IsSuccess.ShouldBeTrue();
        money.Value.Currency.ShouldBe("USD");
    }

    [Fact]
    public void Create_rejects_negative_amount()
        => Money.Create(-1m, "USD").Error.ShouldBe(MoneyErrors.NegativeAmount);

    [Theory]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("DOLLARS")]
    public void Create_rejects_non_iso_currency(string currency)
        => Money.Create(1m, currency).Error.ShouldBe(MoneyErrors.InvalidCurrency);

    [Fact]
    public void Addition_sums_same_currency()
    {
        Money sum = Money.Create(10.50m, "EUR").Value + Money.Create(4.50m, "EUR").Value;

        sum.Amount.ShouldBe(15.00m);
        sum.Currency.ShouldBe("EUR");
    }

    [Fact]
    public void Addition_across_currencies_throws()
        => Should.Throw<InvalidOperationException>(
            () => Money.Create(1m, "EUR").Value + Money.Create(1m, "USD").Value);

    [Fact]
    public void Money_has_value_equality()
        => Money.Create(5m, "USD").Value.ShouldBe(Money.Create(5m, "USD").Value);

    [Fact]
    public void Create_rejects_more_than_two_decimal_places()
        => Money.Create(10.999m, "USD").Error.ShouldBe(MoneyErrors.TooManyDecimalPlaces);

    [Fact]
    public void Subtraction_within_the_same_currency_works()
        => (Money.Create(5m, "USD").Value - Money.Create(3m, "USD").Value)
            .ShouldBe(Money.Create(2m, "USD").Value);

    [Fact]
    public void Subtraction_below_zero_throws_instead_of_minting_negative_money()
        => Should.Throw<InvalidOperationException>(
            () => Money.Create(1m, "USD").Value - Money.Create(5m, "USD").Value);

    [Fact]
    public void Zero_rejects_an_invalid_currency_code()
        => Should.Throw<ArgumentException>(() => Money.Zero("zz1"));

    [Fact]
    public void Zero_normalizes_a_valid_currency_code()
        => Money.Zero("usd").Currency.ShouldBe("USD");
}

public sealed class CurrencyTests
{
    [Fact]
    public void Create_normalizes_casing()
        => Currency.Create("usd").Value.Code.ShouldBe("USD");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("USD1")]
    [InlineData("U5D")]
    public void Create_rejects_non_iso_codes(string? code)
        => Currency.Create(code).Error.ShouldBe(MoneyErrors.InvalidCurrency);
}
