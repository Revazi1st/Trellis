using Trellis.SharedKernel.Results;

namespace Trellis.Persistence.Outbox;

/// <summary>
/// The expected, permanent failures of reading a stored message back (ADR-0004: a <see cref="Result"/>, not
/// an exception). Neither changes on retry — the bytes are what they are — so the processor dead-letters
/// both at once instead of burning attempts on them.
/// </summary>
internal static class OutboxErrors
{
    public const string UnknownEventTypeCode = "Outbox.UnknownEventType";
    public const string PoisonPayloadCode = "Outbox.PoisonPayload";

    public static Error UnknownEventType(string type) => Error.Failure(
        UnknownEventTypeCode,
        $"Unknown event type '{type}': no domain event carries that [DomainEventName]. It was removed or renamed "
        + "while this row was in flight — redeploy a type with that name and re-drive the row.");

    public static Error PoisonPayload(string detail) => Error.Failure(
        PoisonPayloadCode,
        $"Payload does not deserialize and never will: {detail}");
}
