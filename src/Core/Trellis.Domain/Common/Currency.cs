using Trellis.SharedKernel.Results;

namespace Trellis.Domain.Common;

/// <summary>
/// An ISO-4217 currency code as a value object — validated once at the boundary, then carried as a
/// known-good value instead of re-checking three-letter strings everywhere.
/// </summary>
public sealed record Currency
{
    /// <summary>ISO 4217 alphabetic codes are three letters.</summary>
    public const int CodeLength = 3;

    private Currency(string code) => Code = code;

    public string Code { get; }

    public static Result<Currency> Create(string? code)
        => string.IsNullOrWhiteSpace(code) || code.Length != CodeLength || !code.All(char.IsAsciiLetter)
            ? MoneyErrors.InvalidCurrency
            : new Currency(code.ToUpperInvariant());

    public override string ToString() => Code;
}
