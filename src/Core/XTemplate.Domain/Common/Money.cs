using XTemplate.SharedKernel.Results;

namespace XTemplate.Domain.Common;

/// <summary>Amount + ISO-4217 currency. Mapped as an EF complex type (ADR-0006). Cross-currency arithmetic is a programmer error and throws.</summary>
public sealed record Money
{
    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public string Currency { get; }

    public static Result<Money> Create(decimal amount, string currency)
    {
        if (amount < 0)
        {
            return MoneyErrors.NegativeAmount;
        }

        return string.IsNullOrWhiteSpace(currency) || currency.Length != 3
            ? MoneyErrors.InvalidCurrency
            : new Money(amount, currency.ToUpperInvariant());
    }

    public static Money Zero(string currency) => new(0m, currency);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new InvalidOperationException(
                $"Cannot combine {left.Currency} with {right.Currency}; convert explicitly first.");
        }
    }
}
