namespace Trellis.Application.Abstractions;

/// <summary>
/// Ambient tenant override for flows that have no HTTP request but act on behalf of one — the
/// outbox processor restores the originating tenant here before dispatching an event's handlers.
/// Every <see cref="ITenantContext"/> implementation checks this first. AsyncLocal, so the value
/// flows with the async call chain and never leaks across concurrent flows.
/// </summary>
public static class AmbientTenant
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? CurrentTenantId
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}
