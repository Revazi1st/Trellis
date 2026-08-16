using Trellis.SharedKernel.Results;

namespace Trellis.Domain.Common;

public static class MoneyErrors
{
    public static readonly Error NegativeAmount =
        Error.Validation("Money.NegativeAmount", "An amount cannot be negative.");

    public static readonly Error InvalidCurrency =
        Error.Validation("Money.InvalidCurrency", "Currency must be a three-letter ISO 4217 code.");

    public static readonly Error TooManyDecimalPlaces =
        Error.Validation("Money.TooManyDecimalPlaces", "Amounts carry at most two decimal places.");
}
