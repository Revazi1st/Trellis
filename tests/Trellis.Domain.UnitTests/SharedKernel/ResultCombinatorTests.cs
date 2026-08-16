using Shouldly;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.Domain.UnitTests.SharedKernel;

public sealed class ResultCombinatorTests
{
    private static readonly Error ErrorA = Error.Validation("Test.A", "first");
    private static readonly Error ErrorB = Error.Validation("Test.B", "second");

    [Fact]
    public void Bind_chains_on_success_and_short_circuits_on_failure()
    {
        Result.Success(2).Bind(static n => Result.Success(n * 10)).Value.ShouldBe(20);
        Result.Failure<int>(ErrorA).Bind(static n => Result.Success(n * 10)).Error.ShouldBe(ErrorA);
    }

    [Fact]
    public void Map_transforms_only_successes()
    {
        Result.Success(2).Map(static n => n + 1).Value.ShouldBe(3);
        Result.Failure<int>(ErrorA).Map(static n => n + 1).Error.ShouldBe(ErrorA);
    }

    [Fact]
    public void Tap_runs_side_effects_only_on_success()
    {
        int calls = 0;
        Result.Success(1).Tap(_ => calls++).IsSuccess.ShouldBeTrue();
        Result.Failure<int>(ErrorA).Tap(_ => calls++).IsFailure.ShouldBeTrue();
        calls.ShouldBe(1);
    }

    [Fact]
    public void Ensure_fails_a_success_that_breaks_the_predicate()
    {
        Result.Success(5).Ensure(static n => n > 0, ErrorA).IsSuccess.ShouldBeTrue();
        Result.Success(-5).Ensure(static n => n > 0, ErrorA).Error.ShouldBe(ErrorA);
        Result.Failure<int>(ErrorB).Ensure(static n => n > 0, ErrorA).Error.ShouldBe(ErrorB);
    }

    [Fact]
    public async Task Async_overloads_compose_without_intermediate_awaits()
    {
        Result<int> chained = await Task.FromResult(Result.Success(2))
            .Bind(static n => Result.Success(n * 10))
            .Map(static n => n + 1)
            .Tap(static _ => { });

        chained.Value.ShouldBe(21);
    }

    [Fact]
    public void Combine_succeeds_when_everything_succeeded()
        => Result.Combine(Result.Success(), Result.Success(1)).IsSuccess.ShouldBeTrue();

    [Fact]
    public void Combine_with_one_failure_keeps_the_original_error()
        => Result.Combine(Result.Success(), Result.Failure(ErrorA)).Error.ShouldBe(ErrorA);

    [Fact]
    public void Combine_aggregates_multiple_failures_into_one_validation_error()
    {
        Result combined = Result.Combine(Result.Failure(ErrorA), Result.Success(), Result.Failure(ErrorB));

        var validation = combined.Error.ShouldBeOfType<ValidationError>();
        validation.Errors.ShouldBe([ErrorA, ErrorB]);
    }

    [Fact]
    public void Combine_flattens_nested_validation_errors()
    {
        Result combined = Result.Combine(
            Result.Failure(new ValidationError([ErrorA, ErrorB])),
            Result.Failure(ErrorA));

        var validation = combined.Error.ShouldBeOfType<ValidationError>();
        validation.Errors.Count.ShouldBe(3);
    }
}
