using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
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
/// and fails it. The model walk keeps the map complete: every unique index and key is either registered
/// or a fault on purpose with its reason, so a new one cannot ship as an undecided 500.
/// </summary>
public sealed class UniqueConstraintTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    private const string Fingerprint = "0F1E2D3C4B5A69788796A5B4C3D2E1F00F1E2D3C4B5A69788796A5B4C3D2E1F0";

    /// <summary>
    /// The unique constraints whose violation is a defect, each with the reason no two requests can race to
    /// it. A single-column uuid or database-numbered primary key has no row: the walk takes it as minted for
    /// the row (<see cref="IsFaultByItsShape"/>), so a uuid key whose value a request chooses is registered or
    /// listed here by hand. A new unique index or key goes into <c>UniqueConstraintErrors</c> when two
    /// requests can race to it, and here, with its reason, when they cannot.
    /// </summary>
    private static readonly Dictionary<string, string> FaultOnPurpose = new(StringComparer.Ordinal)
    {
        ["RoleNameIndex"] = "RoleManager looks the name up before it inserts, and only the migration job's seed creates roles",
        ["IX_RefreshTokens_TokenHash"] = "the SHA-256 of 32 random bytes (JwtTokenProvider): two equal hashes are a defect, not a race",
        ["PK_AspNetUserRoles"] = "a user gets a role in the transaction that creates it (RegisterAsync), or from the seed after IsInRoleAsync",
        ["PK_AspNetUserLogins"] = "no external sign-in is wired, so nothing writes the table",
        ["PK_AspNetUserTokens"] = "nothing writes the table: refresh tokens live in RefreshTokens",
    };

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

    [Fact]
    public void Every_unique_constraint_in_the_model_is_registered_or_a_fault_on_purpose()
    {
        IModel model = Services.GetRequiredService<AppDbContext>().Model;

        ConstraintAudit audit = Audit(model, UniqueConstraintErrors.RegisteredNames, FaultOnPurpose);

        audit.Undecided.ShouldBeEmpty(
            "a unique constraint nobody decided on: register it in UniqueConstraintErrors (pin its name in the configuration, add the row, race it in a test) when two requests can race to it, or add it to FaultOnPurpose with the reason they cannot");
        audit.NotInModel.ShouldBeEmpty(
            "a name in UniqueConstraintErrors or FaultOnPurpose that the model does not hold: an index or key was renamed or dropped, so its row is stale");
    }

    [Fact]
    public void The_model_walk_flags_an_undecided_constraint_and_a_name_the_model_does_not_hold()
    {
        using var planted = new PlantedContext();

        ConstraintAudit audit = Audit(
            planted.Model,
            registered: ["IX_PlantedCustomers_Code", "IX_PlantedCustomers_Renamed"],
            faultOnPurpose: new Dictionary<string, string>(StringComparer.Ordinal) { ["PK_PlantedGone"] = "a dropped table" });

        // Flagged: the unique e-mail, the uuid alternate key, the string key, the integer key the database
        // does not number and the composite key. Not flagged: the registered code, the uuid primary key and
        // the database-numbered one.
        string[] undecided =
        [
            "AK_PlantedCustomers_ExternalId", "IX_PlantedCustomers_Email", "PK_PlantedCodes", "PK_PlantedNumbers",
            "PK_PlantedTags",
        ];
        string[] notInModel = ["IX_PlantedCustomers_Renamed", "PK_PlantedGone"];
        audit.Undecided.ShouldBe(undecided);
        audit.NotInModel.ShouldBe(notInModel);
    }

    /// <summary>
    /// Every unique constraint of <paramref name="model"/> (keys and unique indexes, by the name PostgreSQL
    /// reports) that is neither registered, listed, nor a fault by its shape, and every registered or listed
    /// name the model does not hold, each sorted.
    /// </summary>
    private static ConstraintAudit Audit(
        IModel model, IEnumerable<string> registered, IReadOnlyDictionary<string, string> faultOnPurpose)
    {
        HashSet<string> decided = new(registered, StringComparer.Ordinal);
        decided.UnionWith(faultOnPurpose.Keys);

        HashSet<string> inModel = new(StringComparer.Ordinal);
        SortedSet<string> undecided = new(StringComparer.Ordinal);
        foreach (IEntityType type in model.GetEntityTypes())
        {
            foreach (IKey key in type.GetDeclaredKeys())
            {
                if (key.GetName() is { } name && inModel.Add(name) && !decided.Contains(name) && !IsFaultByItsShape(key))
                {
                    undecided.Add(name);
                }
            }

            foreach (IIndex index in type.GetDeclaredIndexes().Where(static index => index.IsUnique))
            {
                if (index.GetDatabaseName() is { } name && inModel.Add(name) && !decided.Contains(name))
                {
                    undecided.Add(name);
                }
            }
        }

        return new ConstraintAudit(
            [.. undecided],
            [.. decided.Where(name => !inModel.Contains(name)).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// A single-column primary key taken as minted for the row: any uuid (an ID type's <c>New()</c>,
    /// <c>Guid.CreateVersion7()</c>, EF's generator) or a number the database assigns. Two rows with one are a
    /// defect, not a race. The model cannot tell a uuid the domain mints from one a request chooses (both are
    /// configured <c>ValueGeneratedNever()</c>), so this passes every uuid primary key: an aggregate whose id a
    /// request supplies registers its key in <c>UniqueConstraintErrors</c> or lists it in
    /// <see cref="FaultOnPurpose"/>, and the walk does not ask for it.
    /// </summary>
    private static bool IsFaultByItsShape(IKey key) =>
        key.IsPrimaryKey()
        && key.Properties is [IProperty only]
        && (only.GetColumnType() == "uuid"
            || (only.GetColumnType() is "integer" or "bigint" && only.ValueGenerated == ValueGenerated.OnAdd));

    private sealed record ConstraintAudit(IReadOnlyList<string> Undecided, IReadOnlyList<string> NotInModel);

    /// <summary>
    /// A model built for the control and never connected, as property bags so no entity class goes unused:
    /// the two keys the shape passes (a uuid primary key, a database-numbered one), the single-column keys it
    /// must still flag (a uuid alternate key, a string primary key, an integer one the database does not
    /// number), a composite key and two unique indexes.
    /// </summary>
    private sealed class PlantedContext() : DbContext(
        new DbContextOptionsBuilder<PlantedContext>().UseNpgsql("Host=planted.invalid").Options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.SharedTypeEntity<Dictionary<string, object>>("PlantedCustomers", static customer =>
            {
                customer.Property<Guid>("Id");
                customer.Property<Guid>("ExternalId");
                customer.Property<string>("Email");
                customer.Property<string>("Code");
                customer.HasKey("Id");
                customer.HasAlternateKey("ExternalId");
                customer.HasIndex("Email").IsUnique();
                customer.HasIndex("Code").IsUnique();
            });
            modelBuilder.SharedTypeEntity<Dictionary<string, object>>("PlantedCodes", static code =>
            {
                code.Property<string>("Code");
                code.HasKey("Code");
            });
            modelBuilder.SharedTypeEntity<Dictionary<string, object>>("PlantedNumbers", static number =>
            {
                number.Property<int>("Number").ValueGeneratedNever();
                number.HasKey("Number");
            });
            modelBuilder.SharedTypeEntity<Dictionary<string, object>>("PlantedNotes", static note =>
            {
                note.Property<int>("Id");
                note.HasKey("Id");
            });
            modelBuilder.SharedTypeEntity<Dictionary<string, object>>("PlantedTags", static tag =>
            {
                tag.Property<Guid>("CustomerId");
                tag.Property<string>("Name");
                tag.HasKey("CustomerId", "Name");
            });
        }
    }

    private static IdentityRole<Guid> Role(string name) => new(name)
    {
        Id = Guid.CreateVersion7(),
        NormalizedName = name.ToUpperInvariant(),
    };
}
