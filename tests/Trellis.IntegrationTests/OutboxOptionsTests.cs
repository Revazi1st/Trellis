using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Trellis.Persistence;
using Trellis.Persistence.Outbox;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// The "Outbox" section binds through <c>AddOutboxProcessing</c> exactly as the API host calls it. The
/// cross-field rule runs where the section is bound; the per-property limits are the DataAnnotations
/// the host runs at boot (<c>OptionsConventionTests</c> proves the API registers that validator), so
/// here they are exercised through the BCL validator the options extension itself delegates to.
/// </summary>
public sealed class OutboxOptionsTests
{
    [Fact]
    public void An_absent_section_binds_the_documented_defaults()
    {
        OutboxOptions options = Bind(new Dictionary<string, string?>(StringComparer.Ordinal));

        options.PollInterval.ShouldBe(TimeSpan.FromSeconds(5));
        options.BatchSize.ShouldBe(20);
        options.MaxAttempts.ShouldBe(8);
        options.BaseRetryDelay.ShouldBe(TimeSpan.FromSeconds(30));
        options.MaxRetryDelay.ShouldBe(TimeSpan.FromMinutes(15));
        options.DispatchTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        options.LeaseDuration.ShouldBe(TimeSpan.FromMinutes(10.5));
    }

    [Fact]
    public void A_configured_section_overrides_the_defaults()
    {
        OutboxOptions options = Bind(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Outbox:PollInterval"] = "00:00:01",
            ["Outbox:BatchSize"] = "5",
            ["Outbox:MaxAttempts"] = "3",
            ["Outbox:BaseRetryDelay"] = "00:00:10",
            ["Outbox:MaxRetryDelay"] = "00:01:00",
            ["Outbox:DispatchTimeout"] = "00:00:05",
        });

        options.PollInterval.ShouldBe(TimeSpan.FromSeconds(1));
        options.BatchSize.ShouldBe(5);
        options.MaxAttempts.ShouldBe(3);
        options.BaseRetryDelay.ShouldBe(TimeSpan.FromSeconds(10));
        options.MaxRetryDelay.ShouldBe(TimeSpan.FromMinutes(1));
        options.DispatchTimeout.ShouldBe(TimeSpan.FromSeconds(5));
        options.LeaseDuration.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void A_retry_cap_below_the_first_delay_is_rejected_where_the_section_is_bound()
    {
        OptionsValidationException exception = Should.Throw<OptionsValidationException>(
            () => Bind(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Outbox:MaxRetryDelay"] = "00:00:10" }));

        exception.Message.ShouldContain("Outbox:MaxRetryDelay must be >= Outbox:BaseRetryDelay");
    }

    [Theory]
    [InlineData("PollInterval", "00:00:00")]
    [InlineData("PollInterval", "00:00:00.050")]
    [InlineData("BatchSize", "0")]
    [InlineData("MaxAttempts", "0")]
    [InlineData("MaxAttempts", "51")]
    [InlineData("BaseRetryDelay", "00:00:00")]
    [InlineData("MaxRetryDelay", "2.00:00:00")]
    [InlineData("DispatchTimeout", "00:00:00")]
    public void An_out_of_range_knob_fails_its_declared_limit_naming_the_key(string key, string value)
    {
        OutboxOptions options = Bind(new Dictionary<string, string?>(StringComparer.Ordinal) { [$"Outbox:{key}"] = value });

        List<ValidationResult> failures = [];
        bool valid = Validator.TryValidateObject(options, new ValidationContext(options), failures, validateAllProperties: true);

        valid.ShouldBeFalse();
        failures.ShouldContain(failure => failure.ErrorMessage != null && failure.ErrorMessage.Contains($"Outbox:{key}", StringComparison.Ordinal));
    }

    [Fact]
    public void The_defaults_pass_their_own_declared_limits()
    {
        var options = new OutboxOptions();

        Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true).ShouldBeTrue();
    }

    private static OutboxOptions Bind(Dictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using ServiceProvider provider = new ServiceCollection().AddOutboxProcessing(configuration).BuildServiceProvider();
        return provider.GetRequiredService<IOptions<OutboxOptions>>().Value;
    }
}
