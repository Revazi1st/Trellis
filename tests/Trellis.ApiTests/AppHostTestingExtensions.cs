using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Shouldly;
using Trellis.ServiceDefaults;

namespace Trellis.ApiTests;

/// <summary>
/// Steps a test applies to the AppHost's app model before it builds and starts it. Feature-free, so
/// <c>remove-feature</c> keeps it: an Aspire E2E of your own starts with the same step.
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
    }
}
