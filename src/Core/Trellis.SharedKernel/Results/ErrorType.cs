namespace Trellis.SharedKernel.Results;

/// <summary>Classifies an <see cref="Error"/> so the presentation layer can map it to a transport-specific status (see Api.Common CustomResults).</summary>
public enum ErrorType
{
    Failure = 0,
    Validation = 1,
    NotFound = 2,
    Conflict = 3,
    Unauthorized = 4,
    Forbidden = 5,
}
