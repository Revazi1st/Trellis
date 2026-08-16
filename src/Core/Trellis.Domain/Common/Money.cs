using Trellis.SharedKernel.Results;

namespace Trellis.Domain.Common;

/// <summary>
/// Amount + ISO-4217 currency. Mapped as an EF complex type (ADR-0006). Expected-invalid input
/// flows back as a <see cref="Result"/>; cross-currency arithmetic and negative differences are
/// programmer errors and throw. Persisted with two decimal places, so <see cref="Create(decimal, string)"/>
/// rejects finer scale instead of letting the database truncate it silently.
/// </summary>
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
        => Domain.Common.Currency.Create(currency).Bind(code => Create(amount, code));

    public static Result<Money> Create(decimal amount, Currency currency)
    {
        if (amount < 0)
        {
            return MoneyErrors.NegativeAmount;
        }

        return decimal.Round(amount, 2) != amount
            ? MoneyErrors.TooManyDecimalPlaces
            : new Money(amount, currency.Code);
    }

    /// <summary>Zero in a known-valid currency (e.g. aggregate state). Invalid input here is a programming error.</summary>
    public static Money Zero(string currency)
        => Domain.Common.Currency.Create(currency) is { IsSuccess: true } valid
            ? new Money(0m, valid.Value.Code)
            : throw new ArgumentException($"'{currency}' is not a valid ISO 4217 currency code.", nameof(currency));

    public static Money Zero(Currency currency) => new(0m, currency.Code);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount - right.Amount is { } difference && difference >= 0
            ? new Money(difference, left.Currency)
            : throw new InvalidOperationException(
                $"Subtracting {right.Amount} {right.Currency} from {left.Amount} {left.Currency} would be negative; money never is.");
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
