namespace XTemplate.Api.Contracts.Common;

/// <summary>Custom header names that are part of the public API contract.</summary>
public static class ApiHeaders
{
    /// <summary>Opt-in command idempotency: send a unique value per logical request; retries reuse it.</summary>
    public const string IdempotencyKey = "Idempotency-Key";
}
