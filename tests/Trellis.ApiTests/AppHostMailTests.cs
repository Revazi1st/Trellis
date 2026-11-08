using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Trellis.ServiceDefaults;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// Where the API's e-mail goes under the AppHost, read from the AppHost's app model, which is built and never
/// started: no orchestrator and no container, so this runs in the SDK container beside the other Docker-free tests.
/// Run mode declares Mailpit and points the API's <c>Email</c> section at its SMTP endpoint, in clear and without
/// credentials, as <c>SmtpOptionsValidator</c> allows; publish mode declares no catcher and leaves the section to
/// the deployment.
/// </summary>
public sealed class AppHostMailTests
{
    [Fact]
    public async Task In_run_mode_the_api_sends_its_mail_to_the_Mailpit_catcher()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IDistributedApplicationTestingBuilder appHost =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.Trellis_AppHost>(ct);

        appHost.Resources.ShouldContain(static r => r.Name == ServiceNames.Mail, "the AppHost no longer declares the mail catcher in run mode");
        Dictionary<string, (object Unprocessed, string Processed)> environment = await EnvironmentOfAsync(appHost, ServiceNames.Api, ct);

        environment["Email__Provider"].Processed.ShouldBe("Smtp");
        SmtpEndpoint(environment["Email__Smtp__Host"].Unprocessed).ShouldBe(EndpointProperty.Host);
        SmtpEndpoint(environment["Email__Smtp__Port"].Unprocessed).ShouldBe(EndpointProperty.Port);
        environment["Email__Smtp__UseStartTls"].Processed.ShouldBe("false");
        environment["Email__Smtp__FromAddress"].Processed.ShouldNotBeNullOrWhiteSpace();
        environment.Keys.ShouldNotContain(static key => key == "Email__Smtp__UserName" || key == "Email__Smtp__Password",
            "a catcher in clear takes no credentials: the API would refuse to boot");
    }

    [Fact]
    public async Task In_publish_mode_the_AppHost_declares_no_mail_catcher()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IDistributedApplicationTestingBuilder appHost =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.Trellis_AppHost>(["--operation", "publish"], ct);

        appHost.ExecutionContext.IsPublishMode.ShouldBeTrue();
        appHost.Resources.ShouldNotContain(static r => r.Name == ServiceNames.Mail);
        (await EnvironmentOfAsync(appHost, ServiceNames.Api, ct)).Keys
            .ShouldNotContain(static key => key.StartsWith("Email__", StringComparison.Ordinal), "the deployment configures e-mail");
    }

    /// <summary>
    /// The environment the resource is given, each value as written in the AppHost and as the manifest writes it:
    /// a publish-time evaluation, so an endpoint is its reference rather than a port allocated at run time.
    /// </summary>
    private static async Task<Dictionary<string, (object Unprocessed, string Processed)>> EnvironmentOfAsync(
        IDistributedApplicationTestingBuilder appHost, string resourceName, CancellationToken ct)
    {
        IResource resource = appHost.Resources.Single(r => r.Name == resourceName);
        IExecutionConfigurationResult configuration = await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish), NullLogger.Instance, ct);
        configuration.Exception.ShouldBeNull();
        return configuration.EnvironmentVariablesWithUnprocessed.ToDictionary(static e => e.Key, static e => e.Value, StringComparer.Ordinal);
    }

    /// <summary>The property of Mailpit's SMTP endpoint that <paramref name="value"/> reads, or a failure naming what it is instead.</summary>
    private static EndpointProperty SmtpEndpoint(object value)
    {
        EndpointReferenceExpression endpoint = value.ShouldBeOfType<EndpointReferenceExpression>();
        endpoint.Endpoint.Resource.Name.ShouldBe(ServiceNames.Mail);
        endpoint.Endpoint.EndpointName.ShouldBe("smtp");
        return endpoint.Property;
    }
}
