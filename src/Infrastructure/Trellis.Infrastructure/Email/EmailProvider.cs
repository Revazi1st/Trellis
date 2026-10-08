namespace Trellis.Infrastructure.Email;

/// <summary>The outbound e-mail adapters this build knows. A real provider adds a member and an adapter in <c>Email/</c>.</summary>
public enum EmailProvider
{
    /// <summary>Outbound e-mail switched off on purpose: every message is discarded.</summary>
    None,

    /// <summary>Development stand-in: writes one masked log line per message and sends nothing.</summary>
    Logging,
}
