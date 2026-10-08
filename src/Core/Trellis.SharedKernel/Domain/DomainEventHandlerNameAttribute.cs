namespace Trellis.SharedKernel.Domain;

/// <summary>
/// The handler's stable completion key (e.g. <c>"invoices.paid.send-receipt"</c>). The outbox records
/// the keys of the handlers that already succeeded for a message, so a retry skips them — no second
/// email, no double-counted metric. Renaming or moving the CLR type must not make a credited handler
/// run again, so the key is explicit rather than derived from the type, exactly like
/// <see cref="DomainEventNameAttribute"/> for events. Required on every domain-event handler and unique
/// across them (a fitness test enforces both); never change it while messages are in flight.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DomainEventHandlerNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
