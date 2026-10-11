using Shouldly;
using Trellis.Domain.Common;
using Xunit;

namespace Trellis.Domain.UnitTests.Common;

public sealed class MoneyTests
{
    private static readonly Currency Usd = Currency.Create("USD").Value;

    [Fact]
    public void Create_normalizes_currency_casing()
    {
        var money = Money.Create(10m, "usd");

        money.IsSuccess.ShouldBeTrue();
        money.Value.Currency.ShouldBe(Usd);
        money.Value.Currency.Code.ShouldBe("USD");
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

    [Theory]
    [InlineData("USD", "10.99")]
    [InlineData("JPY", "1000")]
    [InlineData("JPY", "1000.00")]   // trailing zeros are not finer than the minor unit
    [InlineData("KWD", "1.255")]
    [InlineData("CLF", "1.234")]     // four ISO decimals, three stored
    public void Create_accepts_amounts_within_the_currencys_minor_units(string currency, string amount)
        => Money.Create(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), currency).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("USD", "10.999", 2)]
    [InlineData("JPY", "1000.50", 0)]
    [InlineData("KWD", "1.2555", 3)]
    [InlineData("CLF", "1.2345", 3)]   // never finer than the persisted scale, whatever ISO says
    public void Create_rejects_finer_amounts_instead_of_rounding(string currency, string amount, int decimalPlaces)
    {
        Currency code = Currency.Create(currency).Value;

        Money.Create(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), code)
            .Error.ShouldBe(MoneyErrors.TooManyDecimalPlaces(code, decimalPlaces));
    }

    [Fact]
    public void TooManyDecimalPlaces_names_the_currencys_limit()
    {
        MoneyErrors.TooManyDecimalPlaces(Usd, 2).Description.ShouldBe("Amounts in USD carry at most 2 decimal places.");
        MoneyErrors.TooManyDecimalPlaces(Currency.Create("JPY").Value, 0).Description.ShouldBe("Amounts in JPY are whole numbers.");
        MoneyErrors.TooManyDecimalPlaces(Usd, 2).Code.ShouldBe("Money.TooManyDecimalPlaces");
    }

    [Fact]
    public void Create_accepts_the_largest_amount_the_column_holds_and_rejects_more()
    {
        Money.Create(Money.MaxAmount, Currency.Create("KWD").Value).IsSuccess.ShouldBeTrue();
        Money.Create(Money.MaxAmount + 0.001m, Currency.Create("KWD").Value).Error.ShouldBe(MoneyErrors.AmountTooLarge);
        Money.Create(1_000_000_000_000_000m, Usd).Error.ShouldBe(MoneyErrors.AmountTooLarge);
    }

    [Fact]
    public void Addition_sums_same_currency()
    {
        Money sum = Money.Create(10.50m, "EUR").Value + Money.Create(4.50m, "EUR").Value;

        sum.Amount.ShouldBe(15.00m);
        sum.Currency.Code.ShouldBe("EUR");
    }

    [Fact]
    public void Addition_across_currencies_throws()
        => Should.Throw<InvalidOperationException>(
            () => Money.Create(1m, "EUR").Value + Money.Create(1m, "USD").Value);

    [Fact]
    public void Money_has_value_equality()
        => Money.Create(5m, "USD").Value.ShouldBe(Money.Create(5m, "USD").Value);

    [Fact]
    public void Subtraction_within_the_same_currency_works()
        => (Money.Create(5m, "USD").Value - Money.Create(3m, "USD").Value)
            .ShouldBe(Money.Create(2m, "USD").Value);

    [Fact]
    public void Subtraction_below_zero_throws_instead_of_minting_negative_money()
        => Should.Throw<InvalidOperationException>(
            () => Money.Create(1m, "USD").Value - Money.Create(5m, "USD").Value);

    [Fact]
    public void Subtraction_to_exactly_zero_is_allowed()
        => (Money.Create(5m, "USD").Value - Money.Create(5m, "USD").Value).ShouldBe(Money.Zero(Usd));

    [Fact]
    public void Subtraction_across_currencies_throws()
        => Should.Throw<InvalidOperationException>(
            () => Money.Create(5m, "EUR").Value - Money.Create(1m, "USD").Value);

    [Fact]
    public void Addition_of_zero_is_the_identity()
    {
        Money amount = Money.Create(12.34m, "USD").Value;

        (amount + Money.Zero(Usd)).ShouldBe(amount);
        (Money.Zero(Usd) + amount).ShouldBe(amount);
    }

    [Fact]
    public void Operators_return_new_values_and_leave_operands_untouched()
    {
        Money left = Money.Create(10m, "USD").Value;
        Money right = Money.Create(4m, "USD").Value;

        Money sum = left + right;
        Money difference = left - right;

        sum.Amount.ShouldBe(14m);
        difference.Amount.ShouldBe(6m);
        left.Amount.ShouldBe(10m);
        right.Amount.ShouldBe(4m);
    }
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

    [Theory]
    [InlineData("USD", 2)]
    [InlineData("eur", 2)]
    [InlineData("JPY", 0)]
    [InlineData("KRW", 0)]
    [InlineData("KWD", 3)]
    [InlineData("BHD", 3)]
    [InlineData("CLF", 4)]
    [InlineData("ZZZ", 2)]   // a well-formed code ISO does not list carries the common two
    public void MinorUnits_follow_iso_4217(string code, int minorUnits)
        => Currency.Create(code).Value.MinorUnits.ShouldBe(minorUnits);

    [Fact]
    public void Currencies_with_the_same_code_are_equal()
        => Currency.Create("usd").Value.ShouldBe(Currency.Create("USD").Value);
}
