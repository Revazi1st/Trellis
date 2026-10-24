using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Shouldly;
using Trellis.ServiceDefaults;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// <see cref="AppHostTestingExtensions.UseThrowawayDatabase"/>, the step every Aspire E2E applies before it starts the
/// AppHost. Runs without <c>RUN_ASPIRE_E2E</c>: an app model of its own holds postgres the way the AppHost declares it,
/// and is never built or started.
/// </summary>
public sealed class ThrowawayDatabaseTests
{
    /// <summary>
    /// Both lifetime calls are covered because Aspire 13.6 records a persistent lifetime in two annotations, and the
    /// newer one decides: dropping the older one alone leaves the container persistent. The E2E runs the same step on
    /// the real AppHost, whose guards fail if the AppHost stops declaring what is stripped here.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_throwaway_database_keeps_neither_the_data_volume_nor_the_persistent_lifetime(bool olderLifetimeCall)
    {
        IDistributedApplicationBuilder appHost = DistributedApplication.CreateBuilder();
        IResourceBuilder<PostgresServerResource> postgres = appHost.AddPostgres(ServiceNames.Postgres).WithDataVolume();
#pragma warning disable ASPIREPERSISTENCE001 // experimental in Aspire 13.6, as in the AppHost
        if (olderLifetimeCall)
        {
            postgres.WithLifetime(ContainerLifetime.Persistent);
        }
        else
        {
            postgres.WithPersistentLifetime();
        }

        appHost.UseThrowawayDatabase();

        postgres.Resource.Annotations.OfType<ContainerMountAnnotation>().ShouldBeEmpty("the developer's data volume would be mounted");
        postgres.Resource.Annotations.OfType<PersistenceAnnotation>().ShouldBeEmpty("the developer's persistent container would be reused or recreated");
#pragma warning restore ASPIREPERSISTENCE001
        postgres.Resource.Annotations.OfType<ContainerLifetimeAnnotation>().ShouldBeEmpty("the developer's persistent container would be reused or recreated");
    }
}
