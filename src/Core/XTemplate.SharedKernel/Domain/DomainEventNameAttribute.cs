namespace XTemplate.SharedKernel.Domain;

/// <summary>
/// The event's stable wire name (e.g. <c>"invoices.paid.v1"</c>), stored with each outbox row.
/// Renaming or moving the CLR type must never orphan in-flight messages, so the name is explicit
/// and versioned rather than derived from the type. Required on every domain event.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DomainEventNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
