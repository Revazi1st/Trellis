using NetArchTest.Rules;
using Shouldly;

namespace Trellis.ArchitectureTests;

internal static class TestResultExtensions
{
    /// <summary>Asserts the rule held, naming the offending types when it did not.</summary>
    public static void ShouldBeCompliant(this TestResult result)
        => result.IsSuccessful.ShouldBeTrue(
            result.IsSuccessful
                ? null
                : "Offending types: " + string.Join(", ", (result.FailingTypes ?? []).Select(static t => t.FullName)));
}
