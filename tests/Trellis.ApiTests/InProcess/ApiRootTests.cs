using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// The API's own address: in Development it opens the Scalar reference, the page the Aspire dashboard's link
/// leads to as well; outside Development, where neither the reference nor the document is mapped, it is the
/// same 404 problem as any unknown route. The redirect sits outside <c>/api/</c> and out of the OpenAPI
/// document, so <c>EndpointMetadataTests</c> and the committed contract do not see it.
/// </summary>
public sealed class ApiRootTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task The_root_opens_the_API_reference_in_Development()
    {
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location.ShouldNotBeNull().OriginalString.ShouldBe("/scalar/v1");
    }

    [Fact]
    public async Task The_root_is_an_ordinary_404_outside_Development()
    {
        using var production = factory.WithWebHostBuilder(static builder => builder.UseEnvironment(Environments.Production));
        using HttpClient client = production.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.ShouldBeProblemJson();
    }
}
