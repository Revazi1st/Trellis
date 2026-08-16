namespace Trellis.SharedKernel.Domain;

/// <summary>Commit boundary consumed by the UnitOfWork command decorator; implemented by the persistence DbContext.</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
