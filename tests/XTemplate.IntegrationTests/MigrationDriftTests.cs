using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using XTemplate.Persistence;
using Xunit;

namespace XTemplate.IntegrationTests;

public sealed class MigrationDriftTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public void The_model_has_no_pending_migrations()
    {
        // Editing a configuration or the DbContext without scaffolding a migration would otherwise
        // surface as a missing column in production. Scaffold with:
        //   ./scripts/ef.sh migrations add <Name> --project src/Infrastructure/XTemplate.Persistence
        // (pre-release, regenerate InitialCreate instead — see the add-migration skill).
        Services.GetRequiredService<AppDbContext>()
            .Database.HasPendingModelChanges()
            .ShouldBeFalse("the EF model drifted from the migrations snapshot — scaffold a migration");
    }
}
