#if (UseMultitenancy)
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NSubstitute;
using Shouldly;
using Trellis.Application;
using Trellis.Application.Abstractions;
using Trellis.Application.Authorization;
using Trellis.Application.Features.Invoices;
using Trellis.Application.Features.Invoices.ListInvoices;
using Trellis.Domain.Invoices;
using Trellis.Domain.Invoices.Events;
using Trellis.IntegrationTests.Telemetry;
using Trellis.Persistence;
using Trellis.Persistence.Identity;
using Trellis.Persistence.Interceptors;
using Trellis.Persistence.Outbox;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;
using Trellis.SharedKernel.Specifications;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// Tenant isolation against the real database: the interceptor stamps new rows with the tenant and
/// the "Tenant" query filter hides them from everyone else; the model gives every aggregate root
/// that scoping by convention; registration provisions tenants; the outbox carries and restores
/// them (ADR-0022). Compiled only with the multi-tenancy option — the template's all-options CI
/// leg runs it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TenancyTests(DatabaseFixture database) : IAsyncLifetime
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string Password = "Sup3r-secret!";
    private const string TenantACustomer = "Tenant A Corp";
    private const string TenantACustomerEmail = "billing@tenant-a.example";
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly IEmailSender _emailSender = Substitute.For<IEmailSender>();
    private ServiceProvider? _tenantA;
    private ServiceProvider? _tenantB;
    private ServiceProvider? _system;

    public async ValueTask InitializeAsync()
    {
        await database.ResetAsync();
        _tenantA = BuildProvider(new FixedTenantContext(TenantA));
        _tenantB = BuildProvider(new FixedTenantContext(TenantB));
        _system = BuildProvider(new SystemTenantContext());
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        foreach (ServiceProvider? provider in new[] { _tenantA, _tenantB, _system })
        {
            if (provider is not null)
            {
                await provider.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Rows_are_stamped_with_the_tenant_and_hidden_from_other_tenants()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InvoiceId id = await CreateInvoiceAsAsync(_tenantA!);

        using IServiceScope again = _tenantA!.CreateScope();
        (await again.ServiceProvider.GetRequiredService<IInvoiceRepository>().GetByIdAsync(id, ct)).ShouldNotBeNull();

        using IServiceScope other = _tenantB!.CreateScope();
        (await other.ServiceProvider.GetRequiredService<IInvoiceRepository>().GetByIdAsync(id, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task The_invoice_list_returns_only_the_current_tenants_invoices()
    {
        // Through the real pipeline (validation, the handler, the repository's paged query): the "Tenant"
        // filter applies to the count and the page alike, so neither leaks the other tenant's rows.
        CancellationToken ct = TestContext.Current.CancellationToken;
        InvoiceId ofA = await CreateInvoiceAsAsync(_tenantA!);
        InvoiceId ofB;
        using (IServiceScope asTenantB = _tenantB!.CreateScope())
        {
            ofB = await asTenantB.ServiceProvider.CreateInvoiceAsync("Tenant B Corp");
        }

        (await ListInvoicesAsAsync(_tenantA!)).ShouldSatisfyAllConditions(
            page => page.TotalCount.ShouldBe(1),
            page => page.Items.ShouldHaveSingleItem().Id.ShouldBe(ofA.Value));
        (await ListInvoicesAsAsync(_tenantB!)).ShouldSatisfyAllConditions(
            page => page.TotalCount.ShouldBe(1),
            page => page.Items.ShouldHaveSingleItem().Id.ShouldBe(ofB.Value));
        (await ListInvoicesAsAsync(_system!)).TotalCount.ShouldBe(0, "a flow with no tenant sees nothing tenant-scoped");

        async Task<PagedList<InvoiceResponse>> ListInvoicesAsAsync(ServiceProvider provider)
        {
            using IServiceScope scope = provider.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<IQueryHandler<ListInvoicesQuery, PagedList<InvoiceResponse>>>()
                .Handle(new ListInvoicesQuery(), ct)).ShouldBeSuccess();
        }
    }

    [Fact]
    public async Task Ambient_tenant_restores_isolation_for_non_request_flows()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InvoiceId id = await CreateInvoiceAsAsync(_tenantA!);

        // No ambient tenant: system flows see nothing tenant-scoped.
        using (IServiceScope blank = _system!.CreateScope())
        {
            (await blank.ServiceProvider.GetRequiredService<IInvoiceRepository>().GetByIdAsync(id, ct)).ShouldBeNull();
        }

        // The outbox processor sets this before dispatching a tenant's event handlers.
        AmbientTenant.CurrentTenantId = TenantA;
        try
        {
            using IServiceScope restored = _system!.CreateScope();
            (await restored.ServiceProvider.GetRequiredService<IInvoiceRepository>().GetByIdAsync(id, ct)).ShouldNotBeNull();
        }
        finally
        {
            AmbientTenant.CurrentTenantId = null;
        }
    }

    /// <summary>
    /// The fitness test behind the convention: a second aggregate must ship isolated without anyone
    /// remembering to wire it, its unique keys unique per tenant. Runs against the real provider's model.
    /// </summary>
    [Fact]
    public void Every_aggregate_root_is_tenant_scoped_by_convention()
    {
        using IServiceScope scope = _tenantA!.CreateScope();
        IModel model = scope.ServiceProvider.GetRequiredService<AppDbContext>().Model;

        List<IEntityType> aggregates = [.. model.GetEntityTypes()
            .Where(static entityType => typeof(IHasDomainEvents).IsAssignableFrom(entityType.ClrType))];
        aggregates.ShouldContain(static entityType => entityType.ClrType == typeof(Invoice));   // anchors the convention

        foreach (IEntityType aggregate in aggregates)
        {
            ShouldBeTenantScoped(aggregate);
        }
    }

    /// <summary>
    /// The control for the unique-index half of the fitness test, since the real model may hold no unique
    /// index on an aggregate: a probe aggregate declares three the way a configuration would, a plain one,
    /// one sorted descending on every column and one with a pinned name and per-column settings, and the real
    /// <see cref="AppDbContext"/> builds the model. The convention puts TenantId first in each and keeps the
    /// rest, the database name above all: it is what <c>UniqueConstraintErrors</c> and
    /// <c>UniqueConstraintTests.FaultOnPurpose</c> key on.
    /// </summary>
    [Fact]
    public void A_unique_index_on_an_aggregate_is_unique_per_tenant_under_the_name_it_was_given()
    {
        using AppDbContext context = NewProbeContext();
        // The design-time model, the one migrations are generated from: the runtime model drops the
        // per-column sort order.
        IEntityType probe = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(ProbeAccount)).ShouldNotBeNull();

        ShouldBeTenantScoped(probe);
        IIndex[] unique = [.. probe.GetIndexes()
            .Where(static index => index.IsUnique)
            .OrderBy(static index => index.GetDatabaseName(), StringComparer.Ordinal)];
        unique.Select(static index => index.GetDatabaseName())
            .ShouldBe([ProbeModelCustomizer.CodeIndex, ProbeModelCustomizer.EmailCodeIndex, ProbeModelCustomizer.EmailIndex]);
        unique.Select(static index => string.Join(',', index.Properties.Select(static property => property.Name))).ShouldBe(
        [
            $"{TenantInterceptor.TenantIdProperty},Code",
            $"{TenantInterceptor.TenantIdProperty},Email,Code",
            $"{TenantInterceptor.TenantIdProperty},Email",
        ]);

        unique[0].IsDescending.ShouldBeNull();
        unique[1].IsDescending.ShouldBe([false, true, true], "every column the configuration sorted descending still is; the tenant column ascends");
        IIndex email = unique[2];
        email.GetFilter().ShouldBe(ProbeModelCustomizer.EmailFilter);
        email.IsDescending.ShouldBe([false, true]);
        email.GetCollation().ShouldBe([null, ProbeModelCustomizer.EmailCollation], ignoreOrder: false);
        email.GetOperators().ShouldBe([null, ProbeModelCustomizer.EmailOperators], ignoreOrder: false);
        email.GetNullSortOrder().ShouldBe([NullSortOrder.Unspecified, NullSortOrder.NullsLast]);
    }

    /// <summary>
    /// The settings the rewrite keeps must reach what reads them, not only the model, where any list
    /// compares equal to the one asserted above. The C# migration generator writes an annotation only
    /// when it can spell its value (an array; a list type the compiler made up throws on
    /// <c>migrations add</c>), and Npgsql's DDL reads the arrays and drops anything else without a word,
    /// so a case-insensitive key would turn case-sensitive. Read from the probe's own model, nothing connects.
    /// </summary>
    [Fact]
    public void A_tenant_led_unique_index_keeps_its_per_column_settings_in_the_migration_and_the_DDL()
    {
        using AppDbContext context = NewProbeContext();
        IModel model = context.GetService<IDesignTimeModel>().Model;

        // What 'migrations add' hands the C# generator: each per-column setting an array, the tenant first.
        CreateIndexOperation email = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel())
            .OfType<CreateIndexOperation>()
            .Single(static operation => operation.Name == ProbeModelCustomizer.EmailIndex);
        email.Columns.ShouldBe([TenantInterceptor.TenantIdProperty, "Email"]);
        IAnnotation[] perColumn = [.. email.GetAnnotations()
            .Where(static annotation => annotation.Value is System.Collections.IEnumerable and not string)
            .OrderBy(static annotation => annotation.Name, StringComparer.Ordinal)];
        perColumn.Select(static annotation => annotation.Name)
            .ShouldBe(["Npgsql:IndexNullSortOrder", "Npgsql:IndexOperators", RelationalAnnotationNames.Collation]);
        foreach (IAnnotation setting in perColumn)
        {
            setting.Value.ShouldBeAssignableTo<Array>(
                $"{setting.Name} holds a {setting.Value!.GetType().Name}: the C# migration generator writes only an array");
        }

        // What 'dbcontext script', EnsureCreated and a migration's SQL emit: TenantId leads, ascending
        // with no setting of its own, and every column the configuration set keeps its settings (Npgsql
        // quotes a collation only when its name needs it, and probe_ci does not).
        string[] script = context.Database.GenerateCreateScript().ReplaceLineEndings("\n").Split('\n');
        Statement(ProbeModelCustomizer.EmailIndex).ShouldBe(
            $"CREATE UNIQUE INDEX \"{ProbeModelCustomizer.EmailIndex}\" ON \"ProbeAccounts\" " +
            $"(\"{TenantInterceptor.TenantIdProperty}\", \"Email\" COLLATE {ProbeModelCustomizer.EmailCollation} " +
            $"{ProbeModelCustomizer.EmailOperators} DESC NULLS LAST) WHERE {ProbeModelCustomizer.EmailFilter};");
        Statement(ProbeModelCustomizer.EmailCodeIndex).ShouldBe(
            $"CREATE UNIQUE INDEX \"{ProbeModelCustomizer.EmailCodeIndex}\" ON \"ProbeAccounts\" " +
            $"(\"{TenantInterceptor.TenantIdProperty}\", \"Email\" DESC, \"Code\" DESC);");

        string Statement(string index) => script.Single(
            line => line.StartsWith($"CREATE UNIQUE INDEX \"{index}\"", StringComparison.Ordinal)).Trim();
    }

    /// <summary>A context over the probe model (<see cref="ProbeModelCustomizer"/>); nothing connects.</summary>
    private static AppDbContext NewProbeContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=probe.invalid")
            .ReplaceService<IModelCustomizer, ProbeModelCustomizer>()
            .Options,
        new FixedTenantContext(TenantA));

    private static void ShouldBeTenantScoped(IEntityType aggregate)
    {
        string name = aggregate.ClrType.Name;
        aggregate.FindProperty(TenantInterceptor.TenantIdProperty).ShouldNotBeNull($"{name} has no TenantId shadow column");
        aggregate.GetIndexes().ShouldContain(
            static index => index.Properties.Any(static property => property.Name == TenantInterceptor.TenantIdProperty),
            $"{name} has no index on TenantId");
        aggregate.FindDeclaredQueryFilter("Tenant").ShouldNotBeNull($"{name} has no \"Tenant\" query filter");
        foreach (IIndex unique in aggregate.GetIndexes().Where(static index => index.IsUnique))
        {
            unique.Properties[0].Name.ShouldBe(
                TenantInterceptor.TenantIdProperty,
                $"{name}'s unique index {unique.GetDatabaseName()} is unique across every tenant: TenantId must lead it");
        }
    }

    /// <summary>Identity must stay reachable before a tenant is known; children ride their root's filter.</summary>
    [Fact]
    public void Non_aggregate_tables_carry_no_tenant_filter()
    {
        using IServiceScope scope = _tenantA!.CreateScope();
        IModel model = scope.ServiceProvider.GetRequiredService<AppDbContext>().Model;

        List<IEntityType> others = [.. model.GetEntityTypes()
            .Where(static entityType => !typeof(IHasDomainEvents).IsAssignableFrom(entityType.ClrType))];
        others.ShouldContain(static entityType => entityType.ClrType == typeof(ApplicationUser));
        others.ShouldContain(static entityType => entityType.ClrType == typeof(InvoiceLine));
        others.ShouldContain(static entityType => entityType.ClrType == typeof(OutboxMessage));

        foreach (IEntityType other in others)
        {
            other.FindDeclaredQueryFilter("Tenant").ShouldBeNull($"{other.ClrType.Name} must not be tenant-filtered");
            other.GetIndexes().Where(static index => index.IsUnique).ShouldAllBe(
                static index => index.Properties[0].Name != TenantInterceptor.TenantIdProperty,
                $"{other.ClrType.Name}'s unique indexes stay global: one e-mail is one user in one tenant (ADR-0022)");
        }
    }

    [Fact]
    public async Task Registering_provisions_a_fresh_tenant_and_credentials_carry_it()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IServiceScope scope = _system!.CreateScope();

        // Respawn wipes AspNetRoles between tests; registration needs the Member role to exist.
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (await roleManager.FindByNameAsync(Roles.Member) is null)
        {
            IdentitySeed.EnsureSucceeded(
                await roleManager.CreateAsync(new IdentityRole<Guid>(Roles.Member) { Id = Guid.CreateVersion7() }),
                $"Creating role '{Roles.Member}'");
        }

        var identity = scope.ServiceProvider.GetRequiredService<IIdentityService>();
        (await identity.RegisterAsync("first@test.dev", Password, ct)).ShouldBeSuccess();
        (await identity.RegisterAsync("second@test.dev", Password, ct)).ShouldBeSuccess();

        Result<AuthenticatedUser> first = await identity.ValidateCredentialsAsync("first@test.dev", Password, ct);
        Result<AuthenticatedUser> second = await identity.ValidateCredentialsAsync("second@test.dev", Password, ct);

        first.ShouldBeSuccess();
        second.ShouldBeSuccess();
        first.Value.TenantId.ShouldNotBeNullOrWhiteSpace();
        first.Value.TenantId.Length.ShouldBeLessThanOrEqualTo(64);
        second.Value.TenantId.ShouldNotBeNullOrWhiteSpace();
        second.Value.TenantId.Length.ShouldBeLessThanOrEqualTo(64);
        second.Value.TenantId.ShouldNotBe(first.Value.TenantId, StringComparer.Ordinal);
    }

    /// <summary>
    /// A user's TenantId is their membership, assigned by whoever creates the account. The interceptor
    /// stamps only the aggregate roots' shadow column, so a user created while another tenant is
    /// current (an administrator provisioning a tenant, a seeder, an event handler) keeps its own.
    /// </summary>
    [Fact]
    public async Task Creating_a_user_under_another_tenant_keeps_the_users_own_tenant()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string email = "tenant-b-user@test.dev";

        AmbientTenant.CurrentTenantId = TenantA;
        try
        {
            using IServiceScope scope = _system!.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, TenantId = TenantB };
            (await userManager.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
        }
        finally
        {
            AmbientTenant.CurrentTenantId = null;
        }

        using IServiceScope read = _system!.CreateScope();
        ApplicationUser? stored = await read.ServiceProvider.GetRequiredService<AppDbContext>().Users
            .AsNoTracking()
            .SingleOrDefaultAsync(static user => user.Email == email, ct);
        stored.ShouldNotBeNull().TenantId.ShouldBe(TenantB);
    }

    [Fact]
    public async Task Outbox_rows_carry_the_originating_tenant_and_handlers_run_under_it()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string? tenantSeenByHandler = null;
        _emailSender
            .When(static sender => sender.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => tenantSeenByHandler = AmbientTenant.CurrentTenantId);

        InvoiceId id;
        using (IServiceScope scope = _tenantA!.CreateScope())
        {
            id = await scope.ServiceProvider.IssuedInvoiceAsync(customerName: TenantACustomer, customerEmail: TenantACustomerEmail);
        }

        using (IServiceScope scope = _tenantA!.CreateScope())
        {
            await scope.ServiceProvider.PayInvoiceAsync(id);
        }

        // The row remembers who raised the event…
        using (IServiceScope read = _system!.CreateScope())
        {
            AppDbContext context = read.ServiceProvider.GetRequiredService<AppDbContext>();
            OutboxMessage paid = await context.Set<OutboxMessage>().SingleAsync(message => message.Type == "invoices.paid.v1", ct);
            paid.TenantId.ShouldBe(TenantA);
        }

        // …and the processor (a system flow with no request) restores it around the handlers.
        await NewProcessor(_system!).ProcessBatchAsync(ct);

        await _emailSender.Received(1).SendAsync(TenantACustomerEmail, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        tenantSeenByHandler.ShouldBe(TenantA);
        AmbientTenant.CurrentTenantId.ShouldBeNull();
    }

    /// <summary>
    /// The audit's NULL-tenant case: a handler that writes (an invoice here) runs under the originating
    /// tenant, and the outbox commits that write inside the same ambient scope — at the handler's checkpoint
    /// (ADR-0023) — so the TenantInterceptor stamps it with tenant A, never with "no tenant".
    /// </summary>
    [Fact]
    public async Task Handler_writes_are_stamped_with_the_originating_tenant()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using (IServiceScope scope = _tenantA!.CreateScope())
        {
            InvoiceId id = await scope.ServiceProvider.IssuedInvoiceAsync(customerName: TenantACustomer);
            await scope.ServiceProvider.PayInvoiceAsync(id);
        }

        // A system host (no request, no tenant of its own) with a writing handler for the paid event.
        await using ServiceProvider system = BuildProvider(new SystemTenantContext(), static services =>
        {
            services.AddSingleton(new OutboxRetryTests.WritingSwitch { Enabled = true });
            services.AddScoped<IDomainEventHandler<InvoicePaidDomainEvent>, OutboxRetryTests.WritingPaidHandler>();
        });
        (await NewProcessor(system).ProcessBatchAsync(ct)).ShouldBe(2);

        // The handler's invoice belongs to tenant A: visible to A, invisible to B, stamped in the column.
        using (IServiceScope asTenantA = _tenantA!.CreateScope())
        {
            (await HandlerInvoices(asTenantA).AnyAsync(ct)).ShouldBeTrue();
        }

        using (IServiceScope asTenantB = _tenantB!.CreateScope())
        {
            (await HandlerInvoices(asTenantB).AnyAsync(ct)).ShouldBeFalse();
        }

        using (IServiceScope asSystem = _system!.CreateScope())
        {
            (await HandlerInvoices(asSystem)
                    .IgnoreQueryFilters(["Tenant"])
                    .Select(static invoice => EF.Property<string>(invoice, TenantInterceptor.TenantIdProperty))
                    .SingleAsync(ct))
                .ShouldBe(TenantA);
        }

        static IQueryable<Invoice> HandlerInvoices(IServiceScope scope)
            => scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<Invoice>()
                .Where(static invoice => invoice.CustomerName == OutboxRetryTests.WritingPaidHandler.CustomerName);
    }

    private static OutboxProcessor NewProcessor(ServiceProvider host)
        => new(
            host.GetRequiredService<IServiceScopeFactory>(),
            host.GetRequiredService<TimeProvider>(),
            new OutboxOptions(),
            new OutboxMetrics(new TestMeterFactory()),
            NullLogger<OutboxProcessor>.Instance);

    private static async Task<InvoiceId> CreateInvoiceAsAsync(ServiceProvider provider)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider.CreateInvoiceAsync(TenantACustomer);
    }

    private ServiceProvider BuildProvider(ITenantContext tenantContext, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        services.AddSingleton(_emailSender);
        services.AddSingleton<ICurrentUser>(new TestCurrentUser { UserName = "tenancy-tests" });
        services.AddSingleton(tenantContext);
        services.AddApplication();
        services.AddPersistence(database.ConnectionString);
        configure?.Invoke(services);   // after the scanned handlers: a handler added here runs after them
        return services.BuildServiceProvider();
    }

    private sealed class FixedTenantContext(string tenantId) : ITenantContext
    {
        public string? TenantId => AmbientTenant.CurrentTenantId ?? tenantId;
    }

    /// <summary>An aggregate root only the probe model maps; every column is a shadow property.</summary>
    private sealed class ProbeAccount : IHasDomainEvents
    {
        public IReadOnlyList<IDomainEvent> DomainEvents => [];

        public void ClearDomainEvents()
        {
            // The probe raises no events.
        }
    }

    /// <summary>
    /// Maps <see cref="ProbeAccount"/> before <see cref="AppDbContext"/>'s own <c>OnModelCreating</c> runs, as
    /// an entity configuration would, so the tenant convention meets it like any aggregate. The replaced
    /// service gives this context a model cache of its own, and nothing connects.
    /// </summary>
    private sealed class ProbeModelCustomizer(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
    {
        public const string CodeIndex = "IX_ProbeAccounts_Code";
        public const string EmailCodeIndex = "IX_ProbeAccounts_Email_Code";
        public const string EmailIndex = "UX_ProbeAccounts_Email";
        public const string EmailFilter = "\"Email\" <> ''";
        public const string EmailCollation = "probe_ci";
        public const string EmailOperators = "text_pattern_ops";

        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            modelBuilder.Entity<ProbeAccount>(static account =>
            {
                account.ToTable("ProbeAccounts");
                account.Ignore(static probe => probe.DomainEvents);
                account.Property<Guid>("Id");
                account.HasKey("Id");
                account.Property<string>("Code");
                account.Property<string>("Email");
                account.HasIndex("Code").IsUnique();
                account.HasIndex("Email", "Code").IsDescending().IsUnique();
                account.HasIndex("Email")
                    .HasDatabaseName(EmailIndex)
                    .HasFilter(EmailFilter)
                    .IsDescending(true)
                    .UseCollation(EmailCollation)
                    .HasOperators(EmailOperators)
                    .HasNullSortOrder(NullSortOrder.NullsLast)
                    .IsUnique();
            });
            base.Customize(modelBuilder, context);
        }
    }
}
#endif
