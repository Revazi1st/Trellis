using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Trellis.SharedKernel.Decorators;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// The API host binds the outcome line's opt-in from <c>Telemetry:IncludeErrorDetail</c> (ADR-0028): on in
/// Development, where <c>appsettings.Development.json</c> turns it on, and off everywhere else, because an
/// error's description can carry what a user typed and the line is written for every request.
/// </summary>
public sealed class UseCaseLoggingOptionsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public void Development_appends_the_error_description_to_the_outcome_line()
        => factory.Services.GetRequiredService<UseCaseLoggingOptions>().IncludeErrorDetail.ShouldBeTrue();

    [Fact]
    public void Production_leaves_the_description_out()
    {
        using var production = factory.WithWebHostBuilder(static builder => builder.UseEnvironment(Environments.Production));

        production.Services.GetRequiredService<UseCaseLoggingOptions>().IncludeErrorDetail.ShouldBeFalse();
    }

    [Fact]
    public void The_setting_decides_wherever_it_is_given()
    {
        using var turnedOff = factory.WithWebHostBuilder(static builder => builder.UseSetting("Telemetry:IncludeErrorDetail", "false"));

        turnedOff.Services.GetRequiredService<UseCaseLoggingOptions>().IncludeErrorDetail.ShouldBeFalse();
    }
}
