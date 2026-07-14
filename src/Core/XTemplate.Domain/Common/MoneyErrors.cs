using XTemplate.SharedKernel.Results;

namespace XTemplate.Domain.Common;

public static class MoneyErrors
{
    public static readonly Error NegativeAmount =
        Error.Validation("Money.NegativeAmount", "An amount cannot be negative.");

    public static readonly Error InvalidCurrency =
        Error.Validation("Money.InvalidCurrency", "Currency must be a three-letter ISO 4217 code.");
}
