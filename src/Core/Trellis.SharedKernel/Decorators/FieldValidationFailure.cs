using Trellis.SharedKernel.Results;

namespace Trellis.SharedKernel.Decorators;

/// <summary>
/// One FluentValidation failure inside a <see cref="ValidationError"/>, remembering which rule failed so
/// the outcome log line can say <c>CustomerName:Validation.NotEmpty</c>. The wire shape is untouched:
/// <see cref="Error.Code"/> stays the property name and <see cref="Error.Description"/> the message,
/// exactly what the problem document's <c>errors[]</c> carries today.
/// </summary>
internal sealed record FieldValidationFailure : Error
{
    public FieldValidationFailure(string field, string ruleCode, string description)
        : base(field, description, ErrorType.Validation)
    {
        Field = field;
        RuleCode = ruleCode;
    }

    /// <summary>The validated member's path as FluentValidation names it (<c>CustomerName</c>, <c>Lines[0].Amount</c>).</summary>
    public string Field { get; }

    /// <summary>
    /// The failed rule: FluentValidation's built-in codes normalised (<c>NotEmptyValidator</c> →
    /// <c>Validation.NotEmpty</c>); a custom <c>WithErrorCode</c> passes through unchanged.
    /// </summary>
    public string RuleCode { get; }

    internal static string NormalizeRuleCode(string? errorCode)
    {
        const string BuiltInSuffix = "Validator";

        if (string.IsNullOrEmpty(errorCode))
        {
            return "Validation.Invalid";
        }

        return errorCode.EndsWith(BuiltInSuffix, StringComparison.Ordinal) && errorCode.Length > BuiltInSuffix.Length
            ? "Validation." + errorCode[..^BuiltInSuffix.Length]
            : errorCode;
    }
}
