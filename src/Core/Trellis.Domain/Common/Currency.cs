using Trellis.SharedKernel.Results;

namespace Trellis.Domain.Common;

/// <summary>
/// An ISO-4217 currency code as a value object — validated once at the boundary, then carried as a
/// known-good value instead of re-checking three-letter strings everywhere. Mapped as an EF complex
/// type, on its own (<c>Invoice.Currency</c>) and inside <see cref="Money"/>; a single scalar, so it can be
/// mapped as a converted column instead where one needs an index (<see cref="IScalarValueObject{TSelf, TValue}"/>).
/// </summary>
public sealed record Currency : IScalarValueObject<Currency, string>
{
    /// <summary>ISO 4217 alphabetic codes are three letters.</summary>
    public const int CodeLength = 3;

    private Currency(string code) => Code = code;

    public string Code { get; }

    /// <summary>
    /// The decimal places an amount in this currency carries (ISO 4217): 0 for JPY, 3 for KWD, 2 for most.
    /// <see cref="Money"/> refuses an amount finer than this rather than rounding it. Only the exceptions
    /// are listed; any other code (including one ISO does not list, since <see cref="Create"/> checks the
    /// shape, not membership) carries two, ISO's minor unit for most currencies.
    /// </summary>
    public int MinorUnits => Code switch
    {
        // No minor unit: amounts are whole numbers.
        "BIF" or "CLP" or "DJF" or "GNF" or "ISK" or "JPY" or "KMF" or "KRW" or "PYG"
            or "RWF" or "UGX" or "UYI" or "VND" or "VUV" or "XAF" or "XOF" or "XPF" => 0,

        // Thousandths.
        "BHD" or "IQD" or "JOD" or "KWD" or "LYD" or "OMR" or "TND" => 3,

        // Units of account with four decimal places (Money stores at most three; see Money.AmountScale).
        "CLF" or "UYW" => 4,

        _ => 2,
    };

    public static Result<Currency> Create(string? code)
        => string.IsNullOrWhiteSpace(code) || code.Length != CodeLength || !code.All(char.IsAsciiLetter)
            ? MoneyErrors.InvalidCurrency
            : new Currency(code.ToUpperInvariant());

    string IScalarValueObject<Currency, string>.Value => Code;

    static Currency IScalarValueObject<Currency, string>.Restore(string value) => new(value);

    public override string ToString() => Code;
}
