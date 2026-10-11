using System.Text.Json;
using Shouldly;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.Domain.UnitTests.SharedKernel;

public sealed class ResultTests
{
    private static readonly Error TestError = Error.NotFound("Test.NotFound", "Missing.");

    [Fact]
    public void Success_has_no_error()
    {
        Result result = Result.Success();

        result.IsSuccess.ShouldBeTrue();
        result.Error.ShouldBeNull();
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
    public void A_result_never_reaches_json_and_the_refusal_names_the_fix()
    {
        // An endpoint returning Task<Result<T>> would otherwise answer 200 with the Result's internals.
        Result[] results = [Result.Success(), Result.Failure(TestError), Result.Success(42), Result.Failure<int>(TestError)];

        foreach (Result result in results)
        {
            Should.Throw<NotSupportedException>(() => JsonSerializer.Serialize(result, result.GetType()))
                .Message.ShouldContain("CustomResults.Problem");
            Should.Throw<NotSupportedException>(() => JsonSerializer.Serialize<object>(new { body = result }));
        }

        Should.Throw<NotSupportedException>(() => JsonSerializer.Deserialize<Result<int>>("{\"value\":1}"));
    }

    [Fact]
    public void Match_routes_by_outcome()
    {
        Result.Success<int>(2).Match(v => v * 10, _ => -1).ShouldBe(20);
        Result.Failure<int>(TestError).Match(v => v * 10, e => -1).ShouldBe(-1);
    }
}
