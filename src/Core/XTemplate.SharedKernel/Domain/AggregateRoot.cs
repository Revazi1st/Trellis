namespace XTemplate.SharedKernel.Domain;

/// <summary>Consistency boundary. Raise domain events from state transitions; the persistence layer dispatches them on SaveChanges.</summary>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : struct, IStronglyTypedId<TId>
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(TId id)
        : base(id)
    {
    }

    /// <summary>Parameterless constructor reserved for EF Core materialization.</summary>
    protected AggregateRoot()
    {
    }

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
}
