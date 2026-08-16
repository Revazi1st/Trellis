#if (UseMultitenancy)
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using XTemplate.Application;
using XTemplate.Application.Abstractions;
using XTemplate.Application.Features.Invoices.CreateInvoice;
using XTemplate.Domain.Invoices;
using XTemplate.Persistence;
using XTemplate.SharedKernel.Messaging;
using Xunit;

namespace XTemplate.IntegrationTests;

/// <summary>
/// Tenant isolation against the real database: the interceptor stamps new rows with the tenant and
/// the "Tenant" query filter hides them from everyone else. Compiled only with the multi-tenancy
/// option — the template's all-options CI leg runs it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TenancyTests(DatabaseFixture database) : IAsyncLifetime
{
    private ServiceProvider? _tenantA;
    private ServiceProvider? _tenantB;
    private ServiceProvider? _system;

    public async ValueTask InitializeAsync()
    {
        await database.ResetAsync();
        _tenantA = BuildProvider(new FixedTenantContext("tenant-a"));
        _tenantB = BuildProvider(new FixedTenantContext("tenant-b"));
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
        AmbientTenant.CurrentTenantId = "tenant-a";
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

    private static async Task<InvoiceId> CreateInvoiceAsAsync(ServiceProvider provider, CancellationToken ct)
    {
        using IServiceScope scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateInvoiceCommand, InvoiceId>>()
            .Handle(new CreateInvoiceCommand("Tenant A Corp", "a@a.dev", "USD"), ct);
        result.IsSuccess.ShouldBeTrue();
        return result.Value;
    }

    private ServiceProvider BuildProvider(ITenantContext tenantContext)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton(Substitute.For<IEmailSender>());
        services.AddSingleton<ICurrentUser>(new TenantTestUser());
        services.AddSingleton(tenantContext);
        services.AddApplication();
        services.AddPersistence(database.ConnectionString);
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
