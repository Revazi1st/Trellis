namespace XTemplate.SharedKernel.Domain;

public interface IDomainEventDispatcher
{
    Task Dispatch(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken);
}
