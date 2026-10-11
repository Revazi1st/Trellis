using Shouldly;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.Application.UnitTests.SharedKernel;

/// <summary>The implicit conversions handlers lean on to <c>return error;</c> — for both Result shapes.</summary>
public sealed class ResultConversionTests
{
    private static readonly Error Conflict = Error.Conflict("Test.Conflict", "Already done.");

    [Fact]
    public void An_error_converts_to_a_failed_non_generic_Result()
    {
        Result result = Conflict;

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(Conflict);
    }

    [Fact]
    public async Task A_handler_can_return_an_error_directly()
    {
        static async Task<Result> Handle(bool fail)
        {
            await Task.Yield();
            return fail ? Conflict : Result.Success();
        }

        (await Handle(fail: true)).Error.ShouldBe(Conflict);
        (await Handle(fail: false)).ShouldBeSuccess();
    }

    [Fact]
    public void The_generic_conversion_still_targets_the_generic_Result()
    {
        Result<int> result = Conflict;

        result.ShouldBeOfType<Result<int>>().Error.ShouldBe(Conflict);
    }
}
