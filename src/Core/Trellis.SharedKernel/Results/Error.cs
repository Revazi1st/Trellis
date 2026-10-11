namespace Trellis.SharedKernel.Results;

/// <summary>A domain or application error. Prefer static factories on feature-specific error classes (e.g. InvoiceErrors) over ad-hoc instances.</summary>
public record Error(string Code, string Description, ErrorType Type)
{
    public static Error Failure(string code, string description) => new(code, description, ErrorType.Failure);

    public static Error Validation(string code, string description) => new(code, description, ErrorType.Validation);

    public static Error NotFound(string code, string description) => new(code, description, ErrorType.NotFound);

    public static Error Conflict(string code, string description) => new(code, description, ErrorType.Conflict);

    public static Error Unauthorized(string code, string description) => new(code, description, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string description) => new(code, description, ErrorType.Forbidden);

    /// <summary>A dependency is down for now and a later retry may succeed (<see cref="ErrorType.Unavailable"/>).</summary>
    public static Error Unavailable(string code, string description) => new(code, description, ErrorType.Unavailable);

    /// <summary>The caller's expectation about the resource (a version it read) no longer holds (<see cref="ErrorType.PreconditionFailed"/>).</summary>
    public static Error PreconditionFailed(string code, string description) => new(code, description, ErrorType.PreconditionFailed);
}
