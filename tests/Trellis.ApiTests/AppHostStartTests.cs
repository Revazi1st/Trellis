using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Postgres;
using Aspire.Hosting.Testing;
using Shouldly;
using Trellis.ServiceDefaults;
using Trellis.TestSupport.AppHost;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// How a test that starts the AppHost ends a start that cannot succeed, the Aspire E2E and (where the solution
/// has them) the browser tests alike: <see cref="AppHostTestingExtensions.FailureOf"/> says when a resource has
/// failed, <see cref="AppHostTestingExtensions.NeededBy"/> which resources a start watches, and
/// <see cref="AppHostTestingExtensions.RunUnlessOneFailsAsync"/> races a step of the start against that watch.
/// Runs without <c>RUN_ASPIRE_E2E</c> and without Docker: the AppHost's own app model is created and never
/// built, and the watch runs over an application that is built and never started, whose resources' states the
/// test publishes itself. No orchestrator starts.
/// </summary>
public sealed class AppHostStartTests
{
    /// <summary>
    /// Each watch fact's own limit, in milliseconds: a helper that misses a failure it should end on fails its
    /// fact here, with the caller's cancellation, instead of holding the run.
    /// </summary>
    private const int WatchLimit = 30_000;

    private const string Api = ServiceNames.Api;

    public static TheoryData<string, int?, string?> States => new()
    {
        { KnownResourceStates.Running, null, null },
        { KnownResourceStates.Waiting, null, null },
        { KnownResourceStates.NotStarted, null, null },
        // The migration job's success.
        { KnownResourceStates.Finished, 0, null },
        // The API that crashed at boot, as CI saw it.
        { KnownResourceStates.Finished, 134, $"{Api} failed: {KnownResourceStates.Finished}, exit code 134" },
        { KnownResourceStates.Exited, 0, null },
        { KnownResourceStates.Exited, 1, $"{Api} failed: {KnownResourceStates.Exited}, exit code 1" },
        { KnownResourceStates.FailedToStart, null, $"{Api} failed: {KnownResourceStates.FailedToStart}" },
        // Docker is not running.
        { KnownResourceStates.RuntimeUnhealthy, null, $"{Api} failed: {KnownResourceStates.RuntimeUnhealthy}" },
        // Ended, and its exit code not known yet.
        { KnownResourceStates.Finished, null, null },
    };

    [Theory]
    [MemberData(nameof(States))]
    public void A_resource_has_failed_when_it_could_not_start_or_ended_with_a_code_other_than_0(string state, int? exitCode, string? failure)
    {
        CustomResourceSnapshot snapshot = new() { ResourceType = "Project", Properties = [], State = state, ExitCode = exitCode };

        AppHostTestingExtensions.FailureOf(Api, snapshot).ShouldBe(failure);
    }

    /// <summary>
    /// On the AppHost's own app model, created and never built: the front host and everything it waits for,
    /// down to the database server. The tool tiles beside them (pgAdmin, the mail catcher) and the rebuilders
    /// Aspire adds beside each project are not watched, so one of them that crashes does not end a start the
    /// application survives.
    /// </summary>
    [Fact]
    public async Task A_start_watches_the_front_host_and_what_it_waits_for_and_no_tool_tile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IDistributedApplicationTestingBuilder appHost =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.Trellis_AppHost>(ct);

#if (IncludeUI)
        string[] needed = AppHostTestingExtensions.NeededBy(appHost.Resources, ServiceNames.Web);
#else
        string[] needed = AppHostTestingExtensions.NeededBy(appHost.Resources, ServiceNames.Api);
#endif

        needed.ShouldBe(
        [
#if (IncludeUI)
            ServiceNames.Web,
#endif
            ServiceNames.Api,
            ServiceNames.Migrations,
            ServiceNames.Database,
            ServiceNames.Postgres,
#if (UseRedis)
            ServiceNames.Cache,
#endif
        ], ignoreOrder: true);
        string pgAdmin = appHost.Resources.OfType<PgAdminContainerResource>().ShouldHaveSingleItem().Name;
        appHost.Resources.ShouldContain(static r => r.Name == ServiceNames.Mail);
        needed.ShouldNotContain(name => name == pgAdmin);
        needed.ShouldNotContain(static name => name == ServiceNames.Mail);
    }

    /// <summary>
    /// Positive control on a planted model: a chain of a wait for a start, a wait for a completion and a wait for
    /// a child resource is followed to the child's parent, which a child named alone brings too; a tile that
    /// waits for the same server and a resource nothing waits for are left out.
    /// </summary>
    [Fact]
    public void The_needed_resources_follow_every_wait_and_every_parent_and_nothing_else()
    {
        IDistributedApplicationBuilder model = DistributedApplication.CreateBuilder();
        IResourceBuilder<PlantedResource> server = model.AddResource(new PlantedResource("server"));
        IResourceBuilder<PlantedChild> store = model.AddResource(new PlantedChild("store", server.Resource));
        IResourceBuilder<ContainerResource> job = model.AddContainer("job", "planted/job").WaitFor(store);
        IResourceBuilder<ContainerResource> back = model.AddContainer("back", "planted/back").WaitForCompletion(job);
        model.AddContainer("front", "planted/front").WaitFor(back);
        model.AddContainer("tile", "planted/tile").WaitFor(server);
        model.AddContainer("loner", "planted/loner");

        AppHostTestingExtensions.NeededBy(model.Resources, "front").ShouldBe(["front", "back", "job", "store", "server"], ignoreOrder: true);
        AppHostTestingExtensions.NeededBy(model.Resources, "store").ShouldBe(["store", "server"], ignoreOrder: true);
    }

    [Fact(Timeout = WatchLimit)]
    public async Task A_watched_resource_that_fails_cancels_the_step_and_ends_the_start_with_its_sentence()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Planted planted = new(Api, ServiceNames.Web);
        CancellationToken given = default;

        Task start = planted.App.RunUnlessOneFailsAsync([ServiceNames.Web, Api], token =>
        {
            given = token;
            return Task.Delay(Timeout.Infinite, token);
        }, ct);
        await planted.PublishAsync(Api, KnownResourceStates.Finished, exitCode: 134);

        DistributedApplicationException failed = await Should.ThrowAsync<DistributedApplicationException>(start);
        failed.Message.ShouldBe($"{Api} failed: {KnownResourceStates.Finished}, exit code 134");
        given.IsCancellationRequested.ShouldBeTrue("the step runs on behind the failure");
    }

    /// <summary>
    /// An unwatched resource fails first and a watched one after it: the start ends on the second, so the first
    /// was read and passed over. The watch reads changes in the order they were published.
    /// </summary>
    [Fact(Timeout = WatchLimit)]
    public async Task A_resource_that_is_not_watched_does_not_end_the_start_when_it_fails()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string Tile = "pgadmin";
        await using Planted planted = new(Api, Tile);

        Task start = planted.App.RunUnlessOneFailsAsync([Api], static token => Task.Delay(Timeout.Infinite, token), ct);
        await planted.PublishAsync(Tile, KnownResourceStates.Exited, exitCode: 1);
        await planted.PublishAsync(Api, KnownResourceStates.Finished, exitCode: 134);

        (await Should.ThrowAsync<DistributedApplicationException>(start)).Message
            .ShouldBe($"{Api} failed: {KnownResourceStates.Finished}, exit code 134");
    }

    /// <summary>
    /// A step that ends before any watched resource fails is the outcome, a watched resource that changes
    /// without failing meanwhile included: it returns, or its own exception comes out unchanged.
    /// </summary>
    [Fact(Timeout = WatchLimit)]
    public async Task A_step_that_ends_first_is_the_outcome_with_or_without_its_exception()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Planted planted = new(Api);
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task start = planted.App.RunUnlessOneFailsAsync([Api], _ => ended.Task, ct);
        await planted.PublishAsync(Api, KnownResourceStates.Running);
        ended.SetResult();
        await start;

        InvalidOperationException own = await Should.ThrowAsync<InvalidOperationException>(
            () => planted.App.RunUnlessOneFailsAsync([ServiceNames.Web], static _ => Task.FromException(new InvalidOperationException("the step's own")), ct));
        own.Message.ShouldBe("the step's own");
    }

    /// <summary>The caller's cancellation (the fixture's time limit) is a cancellation, not a resource's failure.</summary>
    [Fact(Timeout = WatchLimit)]
    public async Task The_callers_cancellation_surfaces_as_a_cancellation_and_not_as_a_failure()
    {
        await using Planted planted = new(Api);
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task start = planted.App.RunUnlessOneFailsAsync([Api], static token => Task.Delay(Timeout.Infinite, token), caller.Token);
        await caller.CancelAsync();

        Exception? thrown = await Record.ExceptionAsync(() => start);
        thrown.ShouldBeAssignableTo<OperationCanceledException>();
    }

    /// <summary>
    /// An application built and never started (no orchestrator, no Docker), whose resources' states the test
    /// publishes on its notification service as the orchestrator would.
    /// </summary>
    private sealed class Planted : IAsyncDisposable
    {
        private readonly Dictionary<string, IResource> _resources = new(StringComparer.Ordinal);

        public Planted(params string[] names)
        {
            IDistributedApplicationBuilder model = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
            foreach (string name in names)
            {
                _resources[name] = model.AddResource(new PlantedResource(name)).Resource;
            }

            App = model.Build();
        }

        public DistributedApplication App { get; }

        public Task PublishAsync(string name, string state, int? exitCode = null)
            => App.ResourceNotifications.PublishUpdateAsync(_resources[name], snapshot => snapshot with { State = state, ExitCode = exitCode });

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private sealed class PlantedResource(string name) : Resource(name);

    private sealed class PlantedChild(string name, IResource parent) : Resource(name), IResourceWithParent
    {
        public IResource Parent { get; } = parent;
    }
}
