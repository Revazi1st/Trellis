namespace Trellis.SharedKernel.Results;

/// <summary>
/// A validation failure attributed to one member of the message (<c>CustomerEmail</c>,
/// <c>Lines[0].Amount</c>), produced by the validation decorator from FluentValidation failures.
/// <see cref="Error.Code"/> is the failed rule, normalised (<c>NotEmptyValidator</c> → <c>Validation.NotEmpty</c>;
/// an explicit <c>WithErrorCode("Area.Problem")</c> passes through). Domain value-object errors stay plain
/// <see cref="Error"/>s: they describe the request as a whole, not one field. The API seam turns
/// <see cref="Field"/> into the JSON path clients see (<c>lines[0].amount</c>) and a plain error into a
/// <c>field: null</c> entry (ADR-0024).
/// </summary>
public sealed record FieldError(string Field, string Code, string Description)
    : Error(Code, Description, ErrorType.Validation);
