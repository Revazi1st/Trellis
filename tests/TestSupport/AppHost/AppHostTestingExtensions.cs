using System.Globalization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Shouldly;
using Trellis.ServiceDefaults;

namespace Trellis.TestSupport.AppHost;

/// <summary>
/// What a test that starts the AppHost applies to it: a step on the app model before it builds it
/// (<see cref="UseThrowawayDatabase"/>), and a watch over the start itself (<see cref="RunUnlessOneFailsAsync"/>
/// over the resources <see cref="NeededBy"/> names), so that a resource the application needs and that fails
/// ends the start with its own sentence. Feature-free, so <c>remove-feature</c> keeps it: an Aspire E2E of your
/// own starts the same way. Compiled into ApiTests and, with the browser tests, into UiTests (a linked file, as
/// the rest of tests/TestSupport), so it uses only what both reference.
/// </summary>
internal static class AppHostTestingExtensions
{
    /// <summary>
    /// The AppHost gives postgres a named data volume and a persistent lifetime, so <c>aspire run</c> keeps the
    /// data, and the running container, from one run to the next. Both are found by names derived from the AppHost
    /// project (the volume's name, and the container's name plus a hash of the AppHost's path), so without this the
    /// test would open, write to, or recreate the developer's database. Without them every run gets a container of
    /// its own, removed when the test ends, and an empty database that the migration worker creates and seeds.
    /// </summary>
    public static void UseThrowawayDatabase(this IDistributedApplicationBuilder appHost)
    {
        IResource postgres = appHost.Resources.Single(static r => r.Name == ServiceNames.Postgres);
        List<IResourceAnnotation> volumes = [.. postgres.Annotations
            .OfType<ContainerMountAnnotation>()
            .Where(static m => m.Type == ContainerMountType.Volume)];
        volumes.ShouldNotBeEmpty("the AppHost no longer mounts a data volume on postgres; drop that part of UseThrowawayDatabase");

        // WithPersistentLifetime records a PersistenceAnnotation, experimental in Aspire 13.6 like the call itself;
        // the older WithLifetime(ContainerLifetime.Persistent) records that and a ContainerLifetimeAnnotation.
#pragma warning disable ASPIREPERSISTENCE001
        List<IResourceAnnotation> lifetimes = [.. postgres.Annotations.Where(static a =>
            a is PersistenceAnnotation { Mode: not PersistenceMode.Session }
                or ContainerLifetimeAnnotation { Lifetime: ContainerLifetime.Persistent })];
#pragma warning restore ASPIREPERSISTENCE001
        lifetimes.ShouldNotBeEmpty("the AppHost no longer gives postgres a persistent lifetime; drop that part of UseThrowawayDatabase");

        foreach (IResourceAnnotation annotation in volumes.Concat(lifetimes))
        {
            postgres.Annotations.Remove(annotation);
        }

        // Nothing else may outlive the run either. A resource added to the AppHost later with a volume, a
        // writable bind mount or a persistent lifetime would be found by the same derived names, and the test
        // would attach to the developer's own. The step refuses to go on instead: strip that resource here,
        // beside postgres. ThrowawayDatabaseTests runs this on the real app model in every test run.
        KeptBetweenRuns(appHost.Resources).ShouldBeEmpty(
            "a test that starts this AppHost would share these with 'aspire run'; strip them in UseThrowawayDatabase, as postgres is");
    }

    /// <summary>
    /// Each resource that keeps something from one run of the AppHost to the next, as <c>name: what it keeps</c>:
    /// a volume, a bind mount the container may write to, or a persistent lifetime.
    /// </summary>
    internal static string[] KeptBetweenRuns(IEnumerable<IResource> resources)
    {
        List<string> kept = [];
        foreach (IResource resource in resources)
        {
            kept.AddRange(resource.Annotations.OfType<ContainerMountAnnotation>()
                .Where(static mount => mount.Type == ContainerMountType.Volume || !mount.IsReadOnly)
                .Select(mount => mount.Type == ContainerMountType.Volume
                    ? $"{resource.Name}: the volume {mount.Source ?? "(unnamed)"}"
                    : $"{resource.Name}: the writable bind mount {mount.Source}"));
#pragma warning disable ASPIREPERSISTENCE001 // experimental in Aspire 13.6, as above
            if (resource.Annotations.Any(static a =>
                    a is PersistenceAnnotation { Mode: not PersistenceMode.Session }
                        or ContainerLifetimeAnnotation { Lifetime: ContainerLifetime.Persistent }))
#pragma warning restore ASPIREPERSISTENCE001
            {
                kept.Add($"{resource.Name}: a persistent lifetime");
            }
        }

        return [.. kept];
    }

    /// <summary>
    /// <paramref name="names"/> and every resource they wait for, transitively: the resource each
    /// <see cref="WaitAnnotation"/> names (<c>WaitFor</c> and <c>WaitForCompletion</c> alike) and the
    /// <see cref="IResourceWithParent.Parent"/> of a child resource (a database's server). Read from the app
    /// model before it is built. What nothing on that path waits for is left out: a tool tile (pgAdmin, the mail
    /// catcher) and the rebuilder Aspire adds beside each project, so that a tile that crashes does not end a
    /// start that the application itself survives.
    /// </summary>
    internal static string[] NeededBy(IEnumerable<IResource> resources, params string[] names)
    {
        Dictionary<string, IResource> byName = resources.ToDictionary(static resource => resource.Name, StringComparer.Ordinal);
        HashSet<string> needed = new(StringComparer.Ordinal);
        Queue<string> pending = new(names);
        while (pending.TryDequeue(out string? name))
        {
            if (!needed.Add(name) || !byName.TryGetValue(name, out IResource? resource))
            {
                continue;
            }

            foreach (WaitAnnotation wait in resource.Annotations.OfType<WaitAnnotation>())
            {
                pending.Enqueue(wait.Resource.Name);
            }

            if (resource is IResourceWithParent child)
            {
                pending.Enqueue(child.Parent.Name);
            }
        }

        return [.. needed];
    }

    /// <summary>
    /// The sentence that says <paramref name="resource"/> has failed (<c>api failed: Finished, exit code 134</c>),
    /// or null when <paramref name="snapshot"/> shows no failure. Failed is: it could not be started
    /// (<see cref="KnownResourceStates.FailedToStart"/>), its runtime could not start it
    /// (<see cref="KnownResourceStates.RuntimeUnhealthy"/>, Docker not running), or it ended with an exit code
    /// other than 0 (<see cref="KnownResourceStates.Exited"/> or <see cref="KnownResourceStates.Finished"/>). The
    /// migration job ends Finished with 0, which is its success, and a state with no exit code yet is no failure.
    /// </summary>
    internal static string? FailureOf(string resource, CustomResourceSnapshot snapshot)
    {
        string? state = snapshot.State?.Text;
        bool failed = Is(state, KnownResourceStates.FailedToStart)
            || Is(state, KnownResourceStates.RuntimeUnhealthy)
            || ((Is(state, KnownResourceStates.Exited) || Is(state, KnownResourceStates.Finished))
                && snapshot.ExitCode is not null and not 0);
        if (!failed)
        {
            return null;
        }

        return snapshot.ExitCode is { } exitCode
            ? string.Create(CultureInfo.InvariantCulture, $"{resource} failed: {state}, exit code {exitCode}")
            : $"{resource} failed: {state}";
    }

    /// <summary>
    /// Awaits <paramref name="step"/> (the start, a wait for a resource) unless one of the
    /// <paramref name="watched"/> resources fails first. Under the testing builder <c>StartAsync</c> does not
    /// return while a resource waits for a dependency, and it goes on waiting after that dependency has exited
    /// (CI saw the web host wait for an API that had crashed, until the time limit), so this watch is what ends
    /// such a start. It reads every resource's changes (<see cref="ResourceNotificationService.WatchAsync"/>
    /// begins with each one's current state, so a failure from before the step is seen too) and applies
    /// <see cref="FailureOf"/> to the watched names only. A failure that comes first cancels the step, lets it
    /// unwind, and throws a <see cref="DistributedApplicationException"/> whose message is that sentence. A step
    /// that ends first is the outcome, its exception included, and the watch is stopped. The caller's own
    /// cancellation (a time limit) cancels both, and surfaces from the step as a cancellation.
    /// </summary>
    internal static async Task RunUnlessOneFailsAsync(
        this DistributedApplication app,
        IReadOnlyCollection<string> watched,
        Func<CancellationToken, Task> step,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource stepCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenSource watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The step first: one that throws before it returns a task leaves no watch behind. The watch then
        // begins with every resource's current state, so nothing that happened meanwhile is missed.
        Task running = step(stepCancellation.Token);
        Task<string?> failure = FirstFailureAsync(app.ResourceNotifications, watched, watchCancellation.Token);

        if (await Task.WhenAny(running, failure) == failure && failure.IsCompletedSuccessfully && await failure is { } sentence)
        {
            await stepCancellation.CancelAsync();
            await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw new DistributedApplicationException(sentence);
        }

        // The step ended first, or the caller cancelled. The watch is stopped and awaited without throwing: its
        // cancellation is nobody's outcome, and a watch that broke leaves the step to end on its own, under the
        // caller's limit. The step's own outcome is what this returns.
        await watchCancellation.CancelAsync();
        await ((Task)failure).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await running;
    }

    /// <summary>The first watched resource that fails, as <see cref="FailureOf"/> says it; the watch ends only by cancellation.</summary>
    private static async Task<string?> FirstFailureAsync(
        ResourceNotificationService notifications, IReadOnlyCollection<string> watched, CancellationToken cancellationToken)
    {
        HashSet<string> names = new(watched, StringComparer.Ordinal);
        await foreach (ResourceEvent change in notifications.WatchAsync(cancellationToken))
        {
            if (names.Contains(change.Resource.Name) && FailureOf(change.Resource.Name, change.Snapshot) is { } failure)
            {
                return failure;
            }
        }

        return null;
    }

    private static bool Is(string? state, string known) => string.Equals(state, known, StringComparison.Ordinal);
}
