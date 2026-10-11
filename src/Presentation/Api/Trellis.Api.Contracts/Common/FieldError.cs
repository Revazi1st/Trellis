namespace Trellis.Api.Contracts.Common;

/// <summary>One failure inside a 400 <see cref="ProblemResponse"/>: a validation failure, or a body value of the wrong type (<c>Request.InvalidValue</c>).</summary>
/// <param name="Field">
/// JSON path of the offending request member (e.g. <c>customerName</c>, <c>lines[0].amount</c>), or
/// null when the failure is about the request as a whole.
/// </param>
/// <param name="Code">Machine code of this failure (e.g. <c>Validation.NotEmpty</c>, <c>EmailAddress.Invalid</c>).</param>
/// <param name="Description">Human sentence describing the failure; safe to show to the user.</param>
public sealed record FieldError(string? Field, string Code, string Description);
