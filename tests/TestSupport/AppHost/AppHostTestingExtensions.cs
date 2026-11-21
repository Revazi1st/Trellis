using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Shouldly;
using Trellis.ServiceDefaults;

namespace Trellis.TestSupport.AppHost;

/// <summary>
/// Steps a test applies to the AppHost's app model before it builds and starts it. Feature-free, so
/// <c>remove-feature</c> keeps it: an Aspire E2E of your own starts with the same step. Compiled into ApiTests
/// and, with the browser tests, into UiTests (a linked file, as the rest of tests/TestSupport).
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
}
