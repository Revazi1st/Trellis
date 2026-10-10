namespace Trellis.Persistence.Outbox;

/// <summary>
/// The retry schedule: <see cref="OutboxOptions.BaseRetryDelay"/> doubling with every failed attempt,
/// capped at <see cref="OutboxOptions.MaxRetryDelay"/>. A pure function so it has a unit test, and
/// overflow-safe so the largest allowed <see cref="OutboxOptions.MaxAttempts"/> cannot push 2^49 seconds
/// through <see cref="TimeSpan"/>.
/// </summary>
internal static class OutboxBackoff
{
    /// <summary>
    /// 2^20 × the smallest allowed base delay (1 s) is already twelve days, far past the largest allowed cap
    /// (one day), so clamping the exponent here changes no result and only prevents the overflow.
    /// </summary>
    private const int MaxExponent = 20;

    /// <summary>The wait before the next attempt after <paramref name="failedAttempts"/> failures (1 for the first).</summary>
    public static TimeSpan DelayAfter(OutboxOptions options, int failedAttempts)
    {
        ArgumentNullException.ThrowIfNull(options);

        int exponent = Math.Clamp(failedAttempts - 1, 0, MaxExponent);
        double seconds = options.BaseRetryDelay.TotalSeconds * Math.Pow(2, exponent);

        return seconds >= options.MaxRetryDelay.TotalSeconds
            ? options.MaxRetryDelay
            : TimeSpan.FromSeconds(seconds);
    }
}
