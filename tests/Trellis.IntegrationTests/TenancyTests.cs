#if (UseMultitenancy)
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Trellis.Application;
using Trellis.Application.Abstractions;
using Trellis.Application.Authorization;
using Trellis.Application.Features.Invoices.AddInvoiceLine;
using Trellis.Application.Features.Invoices.CreateInvoice;
using Trellis.Application.Features.Invoices.IssueInvoice;
using Trellis.Application.Features.Invoices.PayInvoice;
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
        InvoiceId id = await CreateInvoiceAsAsync(_tenantA!, ct);

        using IServiceScope again = _tenantA!.CreateScope();
        (await again.ServiceProvider.GetRequiredService<IInvoiceRepository>().GetByIdAsync(id, ct)).ShouldNotBeNull();

        using IServiceScope other = _tenantB!.CreateScope();
        (await other.ServiceProvider.GetRequiredService<IInvoiceRepository>().GetByIdAsync(id, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Ambient_tenant_restores_isolation_for_non_request_flows()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InvoiceId id = await CreateInvoiceAsAsync(_tenantA!, ct);

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
    /// remembering to wire it. Runs against the real provider's model.
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
            string name = aggregate.ClrType.Name;
            aggregate.FindProperty(TenantInterceptor.TenantIdProperty).ShouldNotBeNull($"{name} has no TenantId shadow column");
            aggregate.GetIndexes().ShouldContain(
                static index => index.Properties.Any(static property => property.Name == TenantInterceptor.TenantIdProperty),
                $"{name} has no index on TenantId");
            aggregate.FindDeclaredQueryFilter("Tenant").ShouldNotBeNull($"{name} has no \"Tenant\" query filter");
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
            await roleManager.CreateAsync(new IdentityRole<Guid>(Roles.Member) { Id = Guid.CreateVersion7() });
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

        InvoiceId id = await CreateInvoiceAsAsync(_tenantA!, ct);
        using (IServiceScope scope = _tenantA!.CreateScope())
        {
            IServiceProvider services = scope.ServiceProvider;
            (await services.GetRequiredService<ICommandHandler<AddInvoiceLineCommand>>()
                .Handle(new AddInvoiceLineCommand(id, "Line", 10m), ct)).ShouldBeSuccess();
            (await services.GetRequiredService<ICommandHandler<IssueInvoiceCommand>>()
                .Handle(new IssueInvoiceCommand(id, Now.AddDays(14)), ct)).ShouldBeSuccess();
        }

        using (IServiceScope scope = _tenantA!.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ICommandHandler<PayInvoiceCommand>>()
                .Handle(new PayInvoiceCommand(id), ct)).ShouldBeSuccess();
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

        await _emailSender.Received(1).SendAsync("a@a.dev", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        InvoiceId id = await CreateInvoiceAsAsync(_tenantA!, ct);
        using (IServiceScope scope = _tenantA!.CreateScope())
        {
            IServiceProvider services = scope.ServiceProvider;
            (await services.GetRequiredService<ICommandHandler<AddInvoiceLineCommand>>()
                .Handle(new AddInvoiceLineCommand(id, "Line", 10m), ct)).ShouldBeSuccess();
            (await services.GetRequiredService<ICommandHandler<IssueInvoiceCommand>>()
                .Handle(new IssueInvoiceCommand(id, Now.AddDays(14)), ct)).ShouldBeSuccess();
            (await services.GetRequiredService<ICommandHandler<PayInvoiceCommand>>()
                .Handle(new PayInvoiceCommand(id), ct)).ShouldBeSuccess();
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

    private static async Task<InvoiceId> CreateInvoiceAsAsync(ServiceProvider provider, CancellationToken ct)
    {
        using IServiceScope scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateInvoiceCommand, InvoiceId>>()
            .Handle(new CreateInvoiceCommand("Tenant A Corp", "a@a.dev", "USD"), ct);
        result.ShouldBeSuccess();
        return result.Value;
    }

    private ServiceProvider BuildProvider(ITenantContext tenantContext, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        services.AddSingleton(_emailSender);
        services.AddSingleton<ICurrentUser>(new TenantTestUser());
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

    private sealed class TenantTestUser : ICurrentUser
    {
        public Guid? UserId { get; } = Guid.CreateVersion7();

        public string? UserName => "tenancy-tests";
    }
}
#endif
