using Shouldly;
using Xunit;

namespace XTemplate.ArchitectureTests;

public sealed class SanityTests
{
    [Fact]
    public void Test_pipeline_is_wired()
        => true.ShouldBeTrue();
}
