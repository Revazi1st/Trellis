using Trellis.Application.Abstractions;

namespace Trellis.IntegrationTests;

/// <summary>
/// The caller a subcutaneous host runs as: the <see cref="ICurrentUser"/> port, which the API fills from
/// the request. Mutable, so a test switches users between two use cases (one creates, another modifies)
/// or signs out (<c>UserId = null</c>) without building a second host. Each host gets its own instance,
/// so a switch never leaks into another test.
/// </summary>
internal sealed class TestCurrentUser : ICurrentUser
{
    public const string DefaultUserName = "integration-tests";

    public Guid? UserId { get; set; } = Guid.CreateVersion7();

    public string? UserName { get; set; } = DefaultUserName;
}
