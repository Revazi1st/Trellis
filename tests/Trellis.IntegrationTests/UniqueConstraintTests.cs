using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trellis.Application.Abstractions;
using Trellis.Persistence;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// Both sides of the unique-constraint map at the commit boundary (ADR-0018, amendment of 2026-10-11).
/// A constraint registered by name in <c>UniqueConstraintErrors</c> comes back from
/// <see cref="IUnitOfWork.CommitAsync"/> as its <see cref="Result"/>; every other unique violation is a
/// fault the model did not foresee and still throws out of it — a 500 with an Error log, not a guessed 409.
/// A map widened to "any unique violation is a Conflict" turns the first test's exception into a Result
/// and fails it.
/// </summary>
public sealed class UniqueConstraintTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    private const string Fingerprint = "0F1E2D3C4B5A69788796A5B4C3D2E1F00F1E2D3C4B5A69788796A5B4C3D2E1F0";

    [Fact]
    public async Task An_unregistered_unique_violation_throws_out_of_the_commit_and_writes_nothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Identity's RoleNameIndex sits beside its UserNameIndex, which is registered: a map keyed on the
        // table, the "Index" suffix or the SQL state alone would take this one too. RoleManager rejects a
        // duplicate name before it gets here, so one that reaches the database is a defect, not a race.
        Services.GetRequiredService<AppDbContext>().Roles.AddRange(Role("Auditor"), Role("Auditor"));

        DbUpdateException thrown = await Should.ThrowAsync<DbUpdateException>(
            () => Services.GetRequiredService<IUnitOfWork>().CommitAsync(ct));

        PostgresException violation = thrown.InnerException.ShouldBeOfType<PostgresException>();
        violation.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        violation.ConstraintName.ShouldBe("RoleNameIndex");

        using IServiceScope reader = CreateScope();
        (await reader.ServiceProvider.GetRequiredService<AppDbContext>().Roles
            .CountAsync(static role => role.NormalizedName == "AUDITOR", ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_registered_unique_violation_through_the_same_commit_is_its_mapped_Result()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Services.GetRequiredService<IIdempotencyStore>().Reserve("taken", Fingerprint);
        (await Services.GetRequiredService<IUnitOfWork>().CommitAsync(ct)).ShouldBeSuccess();

        // A sequential duplicate, not a race: the commit boundary maps by constraint name, whoever lost.
        using IServiceScope second = CreateScope();
        second.ServiceProvider.GetRequiredService<IIdempotencyStore>().Reserve("taken", Fingerprint);
        Result outcome = await second.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(ct);

        outcome.Error.ShouldBe(IdempotencyErrors.Duplicate);
    }

    private static IdentityRole<Guid> Role(string name) => new(name)
    {
        Id = Guid.CreateVersion7(),
        NormalizedName = name.ToUpperInvariant(),
    };
}
