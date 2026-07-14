using Shouldly;
using XTemplate.SharedKernel.Results;
using Xunit;

namespace XTemplate.Domain.UnitTests.SharedKernel;

public sealed class ResultTests
{
    private static readonly Error TestError = Error.NotFound("Test.NotFound", "Missing.");

    [Fact]
    public void Success_has_no_error()
    {
        Result result = Result.Success();

        result.IsSuccess.ShouldBeTrue();
        result.Error.ShouldBe(Error.None);
    }

    [Fact]
    public void Failure_exposes_error()
    {
        Result result = Result.Failure(TestError);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(TestError);
    }

    [Fact]
    public void Value_on_failure_throws()
    {
        Result<int> result = Result.Failure<int>(TestError);

        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Implicit_conversion_from_value_succeeds()
    {
        Result<int> result = 42;

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void Implicit_conversion_from_error_fails()
    {
        Result<int> result = TestError;

        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Match_routes_by_outcome()
    {
        Result.Success<int>(2).Match(v => v * 10, _ => -1).ShouldBe(20);
        Result.Failure<int>(TestError).Match(v => v * 10, e => -1).ShouldBe(-1);
    }
}
