using Shouldly;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.Domain.UnitTests.SharedKernel;

public sealed class ResultCombinatorTests
{
    private static readonly Error ErrorA = Error.Validation("Test.A", "first");
    private static readonly Error ErrorB = Error.Validation("Test.B", "second");
    private static readonly Error Missing = Error.NotFound("Test.Missing", "not there");
    private static readonly Error Gone = Error.NotFound("Test.Gone", "not there either");
    private static readonly Error Taken = Error.Conflict("Test.Taken", "already there");
    private static readonly Error Broken = Error.Failure("Test.Broken", "server side");
    private static readonly Error Down = Error.Failure("Test.Down", "server side too");

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
        Result.Success(1).Tap(_ => calls++).ShouldBeSuccess();
        Result.Failure<int>(ErrorA).Tap(_ => calls++).IsFailure.ShouldBeTrue();
        calls.ShouldBe(1);
    }

    [Fact]
    public void Ensure_fails_a_success_that_breaks_the_predicate()
    {
        Result.Success(5).Ensure(static n => n > 0, ErrorA).ShouldBeSuccess();
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
        => Result.Combine(Result.Success(), Result.Success(1)).ShouldBeSuccess();

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

    // Merging is for validation errors only: two 404s must not become a 400, a 500 must not hide behind a 404.
    public static TheoryData<Error[], Error> MixedFailures => new()
    {
        { [Missing, Gone], Missing },
        { [ErrorA, Missing], Missing },
        { [Missing, ErrorA, ErrorB], Missing },
        { [new ValidationError([ErrorA, ErrorB]), Taken], Taken },
        { [Missing, Taken], Missing },
        { [ErrorA, Missing, Broken], Broken },
        { [Missing, Broken, Down], Broken },
        { [Broken, ErrorA], Broken },
    };

    [Theory]
    [MemberData(nameof(MixedFailures))]
    public void Combine_with_a_non_validation_failure_returns_the_decisive_error_unmerged(Error[] failures, Error expected)
    {
        Result[] results = [Result.Success(), .. failures.Select(static e => Result.Failure(e))];

        Result.Combine(results).Error.ShouldBe(expected);
    }

    [Fact]
    public void Combine_keeps_a_single_validation_error_as_it_is()
    {
        var aggregate = new ValidationError([ErrorA, ErrorB]);

        Result.Combine(Result.Failure(aggregate), Result.Success(1)).Error.ShouldBeSameAs(aggregate);
    }

    [Fact]
    public void Tuple_combine_hands_both_values_to_a_deconstructing_bind_and_map()
    {
        Result<(int, string)> combined = Result.Combine(Result.Success(2), Result.Success("x"));

        combined.Bind(static (n, s) => Result.Success(s + n)).Value.ShouldBe("x2");
        combined.Map(static (n, s) => n + s.Length).Value.ShouldBe(3);
        Result.Combine(Result.Success(1), Result.Success(2), Result.Success(3))
            .Bind(static (a, b, c) => Result.Success(a + b + c))
            .Map(static sum => sum * 2)
            .Value.ShouldBe(12);
        Result.Combine(Result.Success(1), Result.Success(2), Result.Success(3))
            .Map(static (a, b, c) => $"{a}{b}{c}")
            .Value.ShouldBe("123");
    }

    [Fact]
    public void Tuple_combine_merges_failures_by_the_same_rule_and_never_runs_the_next_step()
    {
        int calls = 0;

        Result<string> validation = Result.Combine(Result.Failure<int>(ErrorA), Result.Failure<string>(ErrorB))
            .Bind((n, s) => { calls++; return Result.Success(s + n); });
        Result<int> mixed = Result.Combine(Result.Failure<int>(ErrorA), Result.Success("x"), Result.Failure<bool>(Missing))
            .Map((n, s, b) => { calls++; return n; });

        validation.Error.ShouldBeOfType<ValidationError>().Errors.ShouldBe([ErrorA, ErrorB]);
        mixed.Error.ShouldBe(Missing);
        calls.ShouldBe(0);
    }

    [Fact]
    public async Task MapOnFailure_replaces_only_a_failure_s_error()
    {
        Result.Failure<int>(Missing).MapOnFailure(static _ => Taken).Error.ShouldBe(Taken);
        Result.Success(1).MapOnFailure(static _ => Taken).Value.ShouldBe(1);

        Result<int> chained = await Task.FromResult(Result.Failure<int>(Missing))
            .MapOnFailure(static e => Error.Unauthorized("Test.Opaque", e.Code))
            .Bind(static n => Result.Success(n + 1));

        chained.Error.ShouldNotBeNull().Description.ShouldBe(Missing.Code);
    }
}
