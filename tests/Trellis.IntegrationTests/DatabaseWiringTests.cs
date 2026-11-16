using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Persistence;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// The two ways a context reaches the provider (<c>PostgresDialect.UseServer</c> and <c>UseDesignTime</c>):
/// a host's registration (<c>AddPersistence</c>) runs under the retrying execution strategy, which is why
/// the outbox claim and the identity transactions run inside <c>CreateExecutionStrategy()</c> (ADR-0023),
/// and the design-time factory does not retry.
/// </summary>
public sealed class DatabaseWiringTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public void A_host_registration_runs_under_the_retrying_execution_strategy()
        => Services.GetRequiredService<AppDbContext>().Database.CreateExecutionStrategy().RetriesOnFailure
            .ShouldBeTrue("AddPersistence(connectionString) turns the provider's retry on; a transient failure would otherwise fail the request");

    [Fact]
    public void The_design_time_context_does_not_retry()
    {
        using AppDbContext context = new AppDbContextFactory().CreateDbContext([]);

        context.Database.CreateExecutionStrategy().RetriesOnFailure.ShouldBeFalse(
            "dotnet ef builds this context and keeps the provider's default strategy, as before the dialect; change this fact together with PostgresDialect.UseDesignTime");
    }
}
