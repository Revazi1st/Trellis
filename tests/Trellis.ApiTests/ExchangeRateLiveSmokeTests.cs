using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Application.Abstractions;
using Trellis.Infrastructure;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// Real calls to the provider the template ships as its default, through the real registration.
/// The stubbed tests prove the adapter maps every outcome; only these notice the provider itself
/// changing under us — a moved URL, a new wire shape, a key now required (the previous default began
/// answering <c>success:false</c> to every keyless call while its TCP probe stayed green), or a new
/// status for a code it does not know, which the adapter maps by this endpoint's contract.
/// <para>
/// Opt-in, because the suite must not depend on the internet or on someone else's uptime: set
/// RUN_LIVE_SMOKE_TESTS=1. Each test is read-only and makes a single request.
/// </para>
/// </summary>
public sealed class ExchangeRateLiveSmokeTests
{
    private const string SkipReason = "Calls the live exchange-rate provider — set RUN_LIVE_SMOKE_TESTS=1 to run it (needs outbound internet).";

    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_LIVE_SMOKE_TESTS"), "1", StringComparison.Ordinal);

    [Fact]
    public async Task GetRate_AgainstTheDefaultProvider_ReturnsAPlausibleRate()
    {
        Assert.SkipUnless(Enabled, SkipReason);
        await using ServiceProvider provider = BuildDefaultProvider();

        Result<decimal> result = await provider.GetRequiredService<IExchangeRateProvider>()
            .GetRateAsync("EUR", "USD", TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue($"the default provider answered {result.Error?.Code}: {result.Error?.Description}");
        result.Value.ShouldBeInRange(0.5m, 2.5m);
    }

    [Fact]
    public async Task GetRate_ForACodeTheDefaultProviderDoesNotKnow_IsUnknownCurrency()
    {
        // Frankfurter answers 422 for an unknown code; a provider that started answering 404 or 500 instead
        // would turn a caller's typo into our outage, and this is where it shows.
        Assert.SkipUnless(Enabled, SkipReason);
        await using ServiceProvider provider = BuildDefaultProvider();

        Result<decimal> result = await provider.GetRequiredService<IExchangeRateProvider>()
            .GetRateAsync("EUR", "XYZ", TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("ExchangeRate.UnknownCurrency", result.Error.Description);
    }

    /// <summary>No ExternalApis section at all: the options' own defaults are what a new project ships with.</summary>
    private static ServiceProvider BuildDefaultProvider()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        services.AddOutboundIntegrations(configuration);
        return services.BuildServiceProvider();
    }
}
