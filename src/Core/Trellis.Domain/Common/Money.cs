using Trellis.SharedKernel.Results;

namespace Trellis.Domain.Common;

/// <summary>
/// Amount + <see cref="Common.Currency"/>. Mapped as an EF complex type (ADR-0006). Expected-invalid input
/// flows back as a <see cref="Result"/>; cross-currency arithmetic and negative differences are
/// programmer errors and throw. An amount carries at most its currency's minor units
/// (<see cref="Common.Currency.MinorUnits"/>) and never more than <see cref="AmountScale"/> decimal places,
/// the scale it is persisted with: <see cref="Create(decimal, Common.Currency)"/> refuses finer input
/// instead of rounding it, and refuses an amount the column cannot hold.
/// </summary>
public sealed record Money
{
    /// <summary>Significant digits persisted (<c>numeric(18,3)</c>).</summary>
    public const int AmountPrecision = 18;

    /// <summary>Decimal places persisted (<c>numeric(18,3)</c>): enough for the three-decimal dinars.</summary>
    public const int AmountScale = 3;

    /// <summary>The largest amount <c>numeric(18,3)</c> holds: fifteen digits before the point, three after.</summary>
    public const decimal MaxAmount = 999_999_999_999_999.999m;

    private Money(decimal amount, Currency currency)
    {
        Amount = amount;
        Currency = currency;
    }

    /// <summary>EF Core materialization only: a nested complex property cannot be bound through a constructor.</summary>
    private Money()
    {
        Currency = null!;
    }

    public decimal Amount { get; }

    public Currency Currency { get; }

    public static Result<Money> Create(decimal amount, string currency)
        => Common.Currency.Create(currency).Bind(code => Create(amount, code));

    public static Result<Money> Create(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        if (amount < 0)
        {
            return MoneyErrors.NegativeAmount;
        }

        if (amount > MaxAmount)
        {
            return MoneyErrors.AmountTooLarge;
        }

        int decimalPlaces = Math.Min(currency.MinorUnits, AmountScale);
        return decimal.Round(amount, decimalPlaces) != amount
            ? MoneyErrors.TooManyDecimalPlaces(currency, decimalPlaces)
            : new Money(amount, currency);
    }

    /// <summary>Zero in a known-valid currency (e.g. aggregate state).</summary>
    public static Money Zero(Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return new Money(0m, currency);
    }

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
