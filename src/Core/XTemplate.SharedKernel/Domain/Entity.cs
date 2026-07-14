namespace XTemplate.SharedKernel.Domain;

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
}
