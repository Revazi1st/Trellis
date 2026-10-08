namespace Trellis.UI.Common.Services;

/// <summary>The circuit's <see cref="INotificationService"/>: one message, hidden after <see cref="ShowFor"/> by a <see cref="TimeProvider"/> timer.</summary>
internal sealed class NotificationService(TimeProvider time) : INotificationService, IDisposable
{
    /// <summary>Long enough to read a short sentence twice; a screen reader announced it when it appeared.</summary>
    public static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(8);

    private readonly Lock _gate = new();
    private ITimer? _expiry;

    public Notification? Current { get; private set; }

    public event EventHandler? Changed;

    public void Success(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var shown = new Notification(message);
        lock (_gate)
        {
            _expiry?.Dispose();
            Current = shown;
            _expiry = time.CreateTimer(_ => Hide(shown), state: null, ShowFor, Timeout.InfiniteTimeSpan);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dismiss()
    {
        if (Current is { } shown)
        {
            Hide(shown);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _expiry?.Dispose();
            _expiry = null;
        }
    }

    /// <summary>Hides <paramref name="shown"/> unless a newer message has replaced it.</summary>
    private void Hide(Notification shown)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(Current, shown))
            {
                return;
            }

            Current = null;
            _expiry?.Dispose();
            _expiry = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
