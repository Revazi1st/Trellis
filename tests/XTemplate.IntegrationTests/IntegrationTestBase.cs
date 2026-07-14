using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using XTemplate.Application;
using XTemplate.Application.Abstractions;
using XTemplate.Persistence;
using Xunit;

namespace XTemplate.IntegrationTests;

/// <summary>
/// Subcutaneous test host: the real Application pipeline + real Persistence against the fixture database,
/// with only the outward-facing ports (email, current user, clock) faked.
/// </summary>
[Collection(DatabaseCollection.Name)]
public abstract class IntegrationTestBase(DatabaseFixture database) : IAsyncLifetime
{
    protected static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
    protected const string CurrentUserName = "integration-tests";

    private ServiceProvider? _provider;
    private IServiceScope? _scope;

    protected IEmailSender EmailSender { get; } = Substitute.For<IEmailSender>();

    protected FakeTimeProvider Time { get; } = new(Now);

    protected IServiceProvider Services => _scope!.ServiceProvider;

    public async ValueTask InitializeAsync()
    {
        await database.ResetAsync();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton(EmailSender);
        services.AddSingleton<ICurrentUser>(new TestCurrentUser());
        services.AddApplication();
        services.AddPersistence(database.ConnectionString);

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
    }

    /// <summary>Fresh scope (fresh DbContext) — use for read-back assertions untainted by the writing scope's change tracker.</summary>
    protected IServiceScope CreateScope() => _provider!.CreateScope();

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        _scope?.Dispose();

        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    private sealed class TestCurrentUser : ICurrentUser
    {
        public Guid? UserId { get; } = Guid.CreateVersion7();

        public string? UserName => CurrentUserName;
    }
}
