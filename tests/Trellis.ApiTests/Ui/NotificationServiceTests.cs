#if (IncludeUI)
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Trellis.UI.Common;
using Trellis.UI.Common.Layout;
using Trellis.UI.Common.Services;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// Success notifications: one message per circuit, replaced by the next, gone when dismissed or when
/// eight seconds pass on the host's clock, and rendered into a live region that is always there.
/// </summary>
public sealed class NotificationServiceTests
{
    private static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task A_message_shows_until_its_time_is_up()
    {
        await using var circuit = new Circuit();
        int changes = 0;
        circuit.Notifications.Changed += (_, _) => changes++;

        circuit.Notifications.Success("Invoice issued.");
        circuit.Notifications.Current.ShouldBe(new Notification("Invoice issued."));

        circuit.Clock.Advance(ShowFor - TimeSpan.FromMilliseconds(1));
        circuit.Notifications.Current.ShouldNotBeNull();

        circuit.Clock.Advance(TimeSpan.FromMilliseconds(1));
        circuit.Notifications.Current.ShouldBeNull();
        changes.ShouldBe(2);
    }

    [Fact]
    public async Task A_newer_message_replaces_the_older_and_keeps_its_own_time()
    {
        await using var circuit = new Circuit();
        circuit.Notifications.Success("Draft invoice created.");
        circuit.Clock.Advance(TimeSpan.FromSeconds(5));

        circuit.Notifications.Success("Invoice issued.");
        circuit.Clock.Advance(TimeSpan.FromSeconds(5));   // the first message's time is up, not this one's

        circuit.Notifications.Current.ShouldBe(new Notification("Invoice issued."));
        circuit.Clock.Advance(TimeSpan.FromSeconds(3));
        circuit.Notifications.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Dismissing_hides_the_message_at_once()
    {
        await using var circuit = new Circuit();
        circuit.Notifications.Success("Invoice voided.");

        circuit.Notifications.Dismiss();

        circuit.Notifications.Current.ShouldBeNull();
        circuit.Clock.PendingTimers.ShouldBe(0);
    }

    [Fact]
    public async Task The_region_is_a_live_region_that_renders_the_message()
    {
        await using var circuit = new Circuit();

        string empty = await circuit.RenderAsync<Notifications>();
        circuit.Notifications.Success("Invoice marked as paid.");
        string shown = await circuit.RenderAsync<Notifications>();

        empty.ShouldContain("role=\"status\"");
        empty.ShouldContain("aria-live=\"polite\"");
        empty.ShouldNotContain("Invoice marked as paid.");
        shown.ShouldContain("Invoice marked as paid.");
        shown.ShouldContain("aria-label=\"Dismiss notification\"");
    }

    /// <summary>One circuit's services over a clock whose timers fire only when the test moves it.</summary>
    private sealed class Circuit : IAsyncDisposable
    {
        private readonly ServiceProvider _root;
        private readonly AsyncServiceScope _scope;

        public Circuit()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddScoped<ITokenStore, InMemoryTokenStore>();
            services.AddSharedUi(static (_, client) => client.BaseAddress = new Uri("http://api.test/"));
            _root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            _scope = _root.CreateAsyncScope();
        }

        public SteppedClock Clock { get; } = new(UiHarness.Start);

        public INotificationService Notifications => _scope.ServiceProvider.GetRequiredService<INotificationService>();

        public async Task<string> RenderAsync<TComponent>()
            where TComponent : IComponent
        {
            await using var renderer = new HtmlRenderer(_scope.ServiceProvider, _root.GetRequiredService<ILoggerFactory>());
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                HtmlRootComponent root = await renderer.RenderComponentAsync<TComponent>();
                return root.ToHtmlString();
            });
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _root.DisposeAsync();
        }
    }

    /// <summary>A <see cref="TimeProvider"/> whose one-shot timers fire when <see cref="Advance"/> reaches them.</summary>
    private sealed class SteppedClock(DateTimeOffset start) : TimeProvider
    {
        private readonly List<SteppedTimer> _timers = [];
        private DateTimeOffset _now = start;

        public int PendingTimers => _timers.Count(static timer => timer.Due is not null);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new SteppedTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (SteppedTimer timer in _timers.ToArray())
            {
                timer.FireIfDue(_now);
            }
        }

        private sealed class SteppedTimer(SteppedClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime;
                return true;
            }

            public void FireIfDue(DateTimeOffset now)
            {
                if (Due is { } due && due <= now)
                {
                    Due = null;
                    callback(state);
                }
            }

            public void Dispose() => Due = null;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
#endif
