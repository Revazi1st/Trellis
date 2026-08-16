namespace Trellis.SharedKernel.Domain;

/// <summary>Non-generic view of an aggregate's pending events; lets persistence interceptors work without knowing TId.</summary>
public interface IHasDomainEvents
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
