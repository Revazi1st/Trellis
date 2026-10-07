using Trellis.SharedKernel.Results;

namespace Trellis.SharedKernel.Domain;

/// <summary>
/// Commit boundary consumed by the UnitOfWork command decorator; implemented by the persistence DbContext.
/// It sits inside the Result boundary (ADR-0004): an expected commit outcome is returned, never thrown.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Commits every pending change in one transaction. A stale optimistic-concurrency token is an
    /// expected outcome and comes back as <see cref="UnitOfWorkErrors.ConcurrencyConflict"/>; only a
    /// genuinely unexpected fault (the database is down, a constraint the model did not foresee) throws.
    /// </summary>
    Task<Result> CommitAsync(CancellationToken cancellationToken);
}
