using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Shouldly;
using Trellis.Domain.Common;
using Trellis.Persistence;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// The converted-scalar rule (ADR-0006, amendment of 2026-10-11). <c>AppDbContext</c> keeps every value
/// object a complex type, and EF Core 10 cannot index a complex-type member; a single-value value object
/// that needs an index is mapped through <see cref="ScalarValueObjectConverter{TValueObject, TValue}"/>
/// instead, one line per type. This probe model does that for <see cref="EmailAddress"/> and
/// <see cref="Currency"/>, in a database of its own on the fixture's server, and shows what it buys (a
/// unique index, <c>Contains</c>) and what it costs (no reaching inside the value in a query).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ScalarValueObjectConverterTests(DatabaseFixture database) : IAsyncLifetime
{
    private const string ProbeDatabase = "converted_scalar_probe";

    private static readonly EmailAddress Jane = EmailAddress.Create("jane@example.com").ShouldBeSuccess();
    private static readonly EmailAddress Omar = EmailAddress.Create("omar@example.com").ShouldBeSuccess();
    private static readonly Currency Usd = Currency.Create("USD").ShouldBeSuccess();

    public async ValueTask InitializeAsync()
    {
        await using ProbeContext context = CreateContext();
        await context.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await using ProbeContext context = CreateContext();
        await context.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void A_converted_value_object_is_one_plain_column_that_carries_an_index()
    {
        using ProbeContext context = CreateContext();
        IEntityType subscriber = context.Model.FindEntityType(typeof(Subscriber)).ShouldNotBeNull();

        IProperty email = subscriber.FindProperty(nameof(Subscriber.Email)).ShouldNotBeNull();
        email.GetValueConverter().ShouldBeOfType<ScalarValueObjectConverter<EmailAddress, string>>();
        // One bounded text column, read from the mapping the provider resolved and not from the annotation
        // the convention wrote: the store type's size, and the type the converter hands the database.
        RelationalTypeMapping column = email.GetRelationalTypeMapping();
        column.Size.ShouldBe(EmailAddress.MaxLength);
        column.Converter.ShouldNotBeNull().ProviderClrType.ShouldBe(typeof(string));
        subscriber.GetComplexProperties().ShouldBeEmpty();

        subscriber.GetIndexes().ShouldHaveSingleItem().Properties.ShouldBe([email]);
    }

    [Fact]
    public async Task The_unique_index_holds_and_Contains_and_equality_translate()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (ProbeContext writer = CreateContext())
        {
            writer.Subscribers.AddRange(new Subscriber(Jane, Usd), new Subscriber(Omar, Usd));
            await writer.SaveChangesAsync(ct);
        }

        await using (ProbeContext duplicate = CreateContext())
        {
            duplicate.Subscribers.Add(new Subscriber(Jane, Usd));
            DbUpdateException refused = await Should.ThrowAsync<DbUpdateException>(() => duplicate.SaveChangesAsync(ct));
            database.Server.ViolatedUniqueConstraint(refused).ShouldNotBeNull("the save was refused, but not by the unique index");
        }

        await using ProbeContext reader = CreateContext();
        EmailAddress[] wanted = [Jane, EmailAddress.Create("nobody@example.com").ShouldBeSuccess()];
        List<Subscriber> found = await reader.Subscribers.Where(s => wanted.Contains(s.Email)).ToListAsync(ct);
        found.ShouldHaveSingleItem().Email.ShouldBe(Jane);

        Subscriber omar = await reader.Subscribers.SingleAsync(s => s.Email == Omar, ct);
        omar.Currency.ShouldBe(Usd);
    }

    [Fact]
    public void Reaching_inside_a_converted_value_object_no_longer_translates()
    {
        using ProbeContext context = CreateContext();

        Should.Throw<InvalidOperationException>(
            () => context.Subscribers.Where(static s => s.Email.Value.StartsWith("jane")).ToQueryString());
    }

    private ProbeContext CreateContext() => new(database.Server.OptionsFor<ProbeContext>(ProbeDatabase));

    private sealed class Subscriber
    {
        public Subscriber(EmailAddress email, Currency currency)
        {
            Id = Guid.CreateVersion7();
            Email = email;
            Currency = currency;
        }

        /// <summary>EF Core materialization only.</summary>
        private Subscriber()
        {
        }

        public Guid Id { get; private set; }

        public EmailAddress Email { get; private set; } = null!;

        public Currency Currency { get; private set; } = null!;
    }

    private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options)
    {
        public DbSet<Subscriber> Subscribers => Set<Subscriber>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Subscriber>(static subscriber =>
            {
                subscriber.HasKey(static s => s.Id);
                subscriber.HasIndex(static s => s.Email).IsUnique();
            });
        }

        // The rule's one line per type, exactly as AppDbContext.ConfigureConventions would carry it.
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            configurationBuilder.Properties<EmailAddress>()
                .HaveConversion<ScalarValueObjectConverter<EmailAddress, string>>().HaveMaxLength(EmailAddress.MaxLength);
            configurationBuilder.Properties<Currency>()
                .HaveConversion<ScalarValueObjectConverter<Currency, string>>().HaveMaxLength(Currency.CodeLength);
        }
    }
}
