using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// The collection of the Aspire end-to-end tests, which run alone, after every parallel collection
/// has finished. The AppHost's processes (the API, the web host, the dashboard, DCP) bind ephemeral
/// ports while Testcontainers asks Docker for host ports from the same range, and the two allocators
/// do not see each other: a test database then fails to start with "failed to listen on TCP socket:
/// address already in use". CI goes further and runs this collection as its own step
/// (<c>--filter-trait Category=AspireE2E</c>), after the other test projects too.
/// An Aspire test of your own joins both: <c>[Collection(AspireE2ECollection.Name)]</c> and
/// <c>[Trait("Category", AspireE2ECollection.Category)]</c>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AspireE2ECollection
{
    public const string Name = "Aspire E2E";
    public const string Category = "AspireE2E";

    private AspireE2ECollection()
    {
    }
}
