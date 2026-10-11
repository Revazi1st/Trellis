using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Application.Abstractions;
using Trellis.Infrastructure;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// One real call to the provider the template ships as its default, through the real registration.
/// The stubbed tests prove the adapter maps every outcome; only this one notices the provider itself
/// changing under us — a moved URL, a new wire shape, a key now required (the previous default began
/// answering <c>success:false</c> to every keyless call while its TCP probe stayed green).
/// <para>
/// Opt-in, because the suite must not depend on the internet or on someone else's uptime: set
/// RUN_LIVE_SMOKE_TESTS=1. It is read-only and makes a single request.
/// </para>
/// </summary>
public sealed class ExchangeRateLiveSmokeTests
{
    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_LIVE_SMOKE_TESTS"), "1", StringComparison.Ordinal);

    [Fact]
    public async Task GetRate_AgainstTheDefaultProvider_ReturnsAPlausibleRate()
    {
        Assert.SkipUnless(Enabled, "Calls the live exchange-rate provider — set RUN_LIVE_SMOKE_TESTS=1 to run it (needs outbound internet).");

        // No ExternalApis section at all: the options' own defaults are what a new project ships with.
        IConfiguration configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        services.AddOutboundIntegrations(configuration);
        await using ServiceProvider provider = services.BuildServiceProvider();

        Result<decimal> result = await provider.GetRequiredService<IExchangeRateProvider>()
            .GetRateAsync("EUR", "USD", TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue($"the default provider answered {result.Error?.Code}: {result.Error?.Description}");
        result.Value.ShouldBeInRange(0.5m, 2.5m);
    }
}
