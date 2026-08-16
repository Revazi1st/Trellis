namespace Trellis.SharedKernel.Domain;

/// <summary>Base type for entities: identity-based equality (same concrete type + same id). Value objects are sealed records instead — they need no base class.</summary>
public abstract class Entity<TId>
    where TId : struct, IStronglyTypedId<TId>
{
    protected Entity(TId id) => Id = id;

    /// <summary>Parameterless constructor reserved for EF Core materialization.</summary>
    protected Entity()
    {
    }

    public TId Id { get; protected init; }

    public override bool Equals(object? obj)
        => obj is Entity<TId> other && other.GetType() == GetType() && other.Id.Equals(Id);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    // The operators mirror Equals: two references to the same identity must never disagree
    // depending on whether the comparison used == or Equals.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Blocker Code Smell", "S3875:\"operator==\" should not be overloaded on reference types",
        Justification = "Entities have identity equality by definition; == diverging from Equals is exactly the bug this prevents.")]
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right)
        => left is null ? right is null : left.Equals(right);

    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);
}
