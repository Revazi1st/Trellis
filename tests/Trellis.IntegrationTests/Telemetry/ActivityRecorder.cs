using System.Diagnostics;
using Trellis.Persistence.Outbox;

namespace Trellis.IntegrationTests.Telemetry;

/// <summary>
/// Collects the outbox dispatch spans (<see cref="OutboxMetrics.ActivitySourceName"/>) as they stop, with
/// all their data recorded, so a test can assert status, tags and events the way a trace backend sees them.
/// Listeners are process-wide: filter on the source name and dispose at the end of the test.
/// </summary>
internal sealed class ActivityRecorder : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _stopped = [];
    private readonly Lock _gate = new();

    public ActivityRecorder()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = static source => string.Equals(source.Name, OutboxMetrics.ActivitySourceName, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            // The processor starts spans from a persisted traceparent string, which goes through this callback.
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_gate)
                {
                    _stopped.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Activity> Stopped
    {
        get
        {
            lock (_gate)
            {
                return [.. _stopped];
            }
        }
    }

    public void Dispose() => _listener.Dispose();
}
