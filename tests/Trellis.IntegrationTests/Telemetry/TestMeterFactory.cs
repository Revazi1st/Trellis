using System.Diagnostics.Metrics;

namespace Trellis.IntegrationTests.Telemetry;

/// <summary>
/// An <see cref="IMeterFactory"/> for one test: every meter it creates carries the factory as its
/// <see cref="Meter.Scope"/>, which is how <see cref="MetricRecorder"/> tells this test's instruments
/// from those of tests running in parallel in the same process.
/// </summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    public Meter Create(MeterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Scope = this;
        return new Meter(options);
    }

    public void Dispose()
    {
        // Meters are owned by their consumers (OutboxMetrics disposes its own).
    }
}
