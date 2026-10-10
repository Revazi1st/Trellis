namespace Trellis.UI.Common.Services;

/// <summary>
/// Tells the user that something they did worked ("Invoice issued."). One message at a time, per circuit
/// (scoped), shown by the layout's <c>Notifications</c> region — a polite live region, so screen readers
/// announce it — until it is dismissed, replaced, or <see cref="NotificationService.ShowFor"/> passes.
/// Failures are not notifications: they stay next to what failed (the form's alert).
/// </summary>
public interface INotificationService
{
    /// <summary>The message on show; null when there is none.</summary>
    Notification? Current { get; }

    /// <summary>Raised whenever <see cref="Current"/> changes, possibly off the renderer's thread.</summary>
    event EventHandler? Changed;

    /// <summary>Shows <paramref name="message"/>, replacing whatever was on show.</summary>
    void Success(string message);

    /// <summary>Hides the message on show, if any.</summary>
    void Dismiss();
}

/// <summary>One message for the user.</summary>
public sealed record Notification(string Message);
