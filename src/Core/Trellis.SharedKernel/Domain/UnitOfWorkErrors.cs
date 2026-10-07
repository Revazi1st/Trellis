using Trellis.SharedKernel.Results;

namespace Trellis.SharedKernel.Domain;

/// <summary>The expected outcomes of <see cref="IUnitOfWork.CommitAsync"/> other than success.</summary>
public static class UnitOfWorkErrors
{
    /// <summary>
    /// Someone else changed the aggregate between this request's read and its commit (a stale
    /// optimistic-concurrency token). A 409 through <c>CustomResults</c>: the client reloads and retries.
    /// </summary>
    public static readonly Error ConcurrencyConflict =
        Error.Conflict("Concurrency.Conflict", "The resource was modified by someone else. Reload and try again.");
}
