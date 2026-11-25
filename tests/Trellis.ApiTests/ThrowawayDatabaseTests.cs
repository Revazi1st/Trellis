using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Shouldly;
using Trellis.ServiceDefaults;
using Trellis.TestSupport.AppHost;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// <see cref="AppHostTestingExtensions.UseThrowawayDatabase"/>, the step every test that starts the AppHost applies
/// before it builds it (the Aspire E2E and, where the solution has them, the browser tests). Runs without
/// <c>RUN_ASPIRE_E2E</c> and without Docker: the app models here, the AppHost's own among them, are created and never
/// built or started.
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

    /// <summary>
    /// The step on the AppHost's own app model. It strips postgres, the one resource the AppHost keeps between runs
    /// today, and refuses an app model in which anything else is kept: so a resource added later with a volume or a
    /// persistent lifetime turns this red in an ordinary test run, before a test that starts the AppHost can attach to
    /// the container or the data <c>aspire run</c> keeps.
    /// </summary>
    [Fact]
    public async Task After_the_step_nothing_in_the_AppHost_outlives_a_test_run()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IDistributedApplicationTestingBuilder appHost =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.Trellis_AppHost>(ct);

        appHost.UseThrowawayDatabase();

        appHost.Resources.ShouldContain(static r => r.Name == ServiceNames.Postgres);
        AppHostTestingExtensions.KeptBetweenRuns(appHost.Resources).ShouldBeEmpty();
    }

    /// <summary>
    /// Positive control: beside a postgres declared as the AppHost declares it, a resource with a named volume, one
    /// with a persistent lifetime and one with a bind mount it may write to are each named, and the step refuses the
    /// app model; a read-only bind mount (a configuration file) and a resource that keeps nothing are not named.
    /// </summary>
    [Fact]
    public void The_step_refuses_an_AppHost_that_keeps_another_resource_between_runs()
    {
        IDistributedApplicationBuilder appHost = DistributedApplication.CreateBuilder();
#pragma warning disable ASPIREPERSISTENCE001 // experimental in Aspire 13.6, as in the AppHost
        appHost.AddPostgres(ServiceNames.Postgres).WithDataVolume().WithPersistentLifetime();
#pragma warning restore ASPIREPERSISTENCE001
        appHost.AddContainer("broker", "planted/broker").WithVolume("broker-data", "/var/lib/broker");
        appHost.AddContainer("identity", "planted/identity").WithLifetime(ContainerLifetime.Persistent);
        appHost.AddContainer("files", "planted/files").WithBindMount("/srv/files", "/data");
        appHost.AddContainer("realm", "planted/realm").WithBindMount("/srv/realm.json", "/import/realm.json", isReadOnly: true);
        appHost.AddContainer("catcher", "planted/catcher");

        string message = Should.Throw<ShouldAssertException>(() => appHost.UseThrowawayDatabase()).Message;

        message.ShouldContain("strip them in UseThrowawayDatabase");
        AppHostTestingExtensions.KeptBetweenRuns(appHost.Resources).ShouldBe(
        [
            "broker: the volume broker-data",
            "identity: a persistent lifetime",
            "files: the writable bind mount /srv/files",
        ]);
    }
}
