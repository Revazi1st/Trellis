using System.Diagnostics.Metrics;

namespace Trellis.IntegrationTests.Telemetry;

/// <summary>
/// Records every measurement of the instruments a <see cref="TestMeterFactory"/> created — counters and
/// histograms as they are published, gauges when <see cref="RecordObservableInstruments"/> is called —
/// so a test asserts on the exported numbers, not on the code that increments them.
/// </summary>
internal sealed class MetricRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<Measurement> _measurements = [];
    private readonly Lock _gate = new();

    public MetricRecorder(TestMeterFactory factory)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, factory))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    /// <summary>Polls the observable gauges, the way an exporter does on its schedule.</summary>
    public void RecordObservableInstruments() => _listener.RecordObservableInstruments();

    /// <summary>Every measurement of <paramref name="instrument"/>, in publication order.</summary>
    public IReadOnlyList<Measurement> Of(string instrument)
    {
        lock (_gate)
        {
            return [.. _measurements.Where(m => string.Equals(m.Instrument, instrument, StringComparison.Ordinal))];
        }
    }

    /// <summary>The sum of a counter's measurements whose tags include every pair given.</summary>
    public double Total(string instrument, params KeyValuePair<string, object?>[] tags)
        => Of(instrument)
            .Where(m => tags.All(tag => m.Tags.TryGetValue(tag.Key, out object? value) && Equals(value, tag.Value)))
            .Sum(static m => m.Value);

    /// <summary>The most recent value of a gauge (or any instrument), or null when it was never observed.</summary>
    public double? Last(string instrument) => Of(instrument) is { Count: > 0 } all ? all[^1].Value : null;

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        // The span is only valid during the callback: copy the tags before storing anything.
        var copied = new Dictionary<string, object?>(tags.Length, StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            copied[tag.Key] = tag.Value;
        }

        lock (_gate)
        {
            _measurements.Add(new Measurement(instrument.Name, value, copied));
        }
    }

    public sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);
}
