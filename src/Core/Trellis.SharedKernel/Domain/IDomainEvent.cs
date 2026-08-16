namespace Trellis.SharedKernel.Domain;

/// <summary>Marker for domain events. Deliberately carries no timestamp — the domain stays pure; auditing stamps time at persistence.</summary>
public interface IDomainEvent;
