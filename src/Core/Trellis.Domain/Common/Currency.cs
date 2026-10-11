using System.Collections.Frozen;
using Trellis.SharedKernel.Results;

namespace Trellis.Domain.Common;

/// <summary>
/// An ISO-4217 currency code as a value object — validated once at the boundary, then carried as a
/// known-good value instead of re-checking three-letter strings everywhere. Mapped as an EF complex
/// type, on its own (<c>Invoice.Currency</c>) and inside <see cref="Money"/>.
/// </summary>
public sealed record Currency
{
    /// <summary>ISO 4217 alphabetic codes are three letters.</summary>
    public const int CodeLength = 3;

    /// <summary>ISO 4217's minor unit for most currencies: cents, pence, øre.</summary>
    private const int DefaultMinorUnits = 2;

    /// <summary>
    /// The ISO 4217 currencies whose minor unit is not two decimal places. Any other code — including one
    /// ISO does not list, since <see cref="Create"/> checks the shape, not membership — carries two.
    /// </summary>
    private static readonly FrozenDictionary<string, int> MinorUnitExceptions = new (string[] Codes, int MinorUnits)[]
    {
        // No minor unit: amounts are whole numbers.
        (["BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW", "PYG", "RWF", "UGX", "UYI", "VND", "VUV", "XAF", "XOF", "XPF"], 0),

        // Thousandths.
        (["BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND"], 3),

        // Units of account with four decimal places (Money stores at most three; see Money.AmountScale).
        (["CLF", "UYW"], 4),
    }
    .SelectMany(static group => group.Codes.Select(code => KeyValuePair.Create(code, group.MinorUnits)))
    .ToFrozenDictionary(StringComparer.Ordinal);

    private Currency(string code) => Code = code;

    public string Code { get; }

    /// <summary>
    /// The decimal places an amount in this currency carries (ISO 4217): 0 for JPY, 3 for KWD, 2 for most.
    /// <see cref="Money"/> refuses an amount finer than this rather than rounding it.
    /// </summary>
    public int MinorUnits => MinorUnitExceptions.GetValueOrDefault(Code, DefaultMinorUnits);

    public static Result<Currency> Create(string? code)
        => string.IsNullOrWhiteSpace(code) || code.Length != CodeLength || !code.All(char.IsAsciiLetter)
            ? MoneyErrors.InvalidCurrency
            : new Currency(code.ToUpperInvariant());

    public override string ToString() => Code;
}
