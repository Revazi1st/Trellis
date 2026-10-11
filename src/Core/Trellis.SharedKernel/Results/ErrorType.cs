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

    /// <summary>
    /// Something this operation depends on is down for now (a third party unreachable, timing out, or
    /// refused by our own circuit breaker): the same request may succeed later. Not for a dependency that
    /// rejected us (bad credentials, a wrong URL), which no wait cures: that stays <see cref="Failure"/>.
    /// </summary>
    Unavailable = 6,

    /// <summary>
    /// A condition the caller attached to the request no longer holds: it expected a version of the
    /// resource the resource has moved on from. The caller reloads and decides again; nothing was changed.
    /// </summary>
    PreconditionFailed = 7,
}
