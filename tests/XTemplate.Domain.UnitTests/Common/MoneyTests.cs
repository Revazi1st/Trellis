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
}
