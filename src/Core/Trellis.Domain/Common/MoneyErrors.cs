using System.Globalization;
using Trellis.SharedKernel.Results;

namespace Trellis.Domain.Common;

public static class MoneyErrors
{
    public static readonly Error NegativeAmount =
        Error.Validation("Money.NegativeAmount", "An amount cannot be negative.");

    public static readonly Error InvalidCurrency =
        Error.Validation("Money.InvalidCurrency", "Currency must be a three-letter ISO 4217 code.");

    public static readonly Error AmountTooLarge = Error.Validation(
        "Money.AmountTooLarge",
        string.Create(CultureInfo.InvariantCulture, $"An amount is at most {Money.MaxAmount}."));

    /// <summary>One code whatever the currency; the description names the currency's limit.</summary>
    public static Error TooManyDecimalPlaces(Currency currency, int decimalPlaces)
    {
        ArgumentNullException.ThrowIfNull(currency);

        return Error.Validation(
            "Money.TooManyDecimalPlaces",
            decimalPlaces == 0
                ? $"Amounts in {currency.Code} are whole numbers."
                : string.Create(CultureInfo.InvariantCulture, $"Amounts in {currency.Code} carry at most {decimalPlaces} decimal places."));
    }
}
