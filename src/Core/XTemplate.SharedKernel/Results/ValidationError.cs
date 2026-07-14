namespace XTemplate.SharedKernel.Results;

/// <summary>Aggregates per-field validation failures into a single error; produced by the validation decorator.</summary>
public sealed record ValidationError : Error
{
    public ValidationError(IReadOnlyCollection<Error> errors)
        : base("Validation.General", "One or more validation errors occurred.", ErrorType.Validation)
        => Errors = errors;

    public IReadOnlyCollection<Error> Errors { get; }
}
