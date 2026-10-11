using Shouldly;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.Domain.UnitTests.SharedKernel;

/// <summary><c>AtField</c>: a handler names the request member a domain validation error belongs to (ADR-0024 amendment).</summary>
public sealed class ResultAtFieldTests
{
    private static readonly Error Invalid = Error.Validation("Thing.Invalid", "The thing is not valid.");
    private static readonly Error TooLong = Error.Validation("Thing.TooLong", "The thing is too long.");
    private static readonly Error Missing = Error.NotFound("Thing.Missing", "No such thing.");

    [Fact]
    public void AtField_PlainValidationError_BecomesAFieldErrorKeepingCodeAndDescription()
    {
        Result<int> result = Result.Failure<int>(Invalid).AtField("Thing");

        result.Error.ShouldBe(new FieldError("Thing", Invalid.Code, Invalid.Description));
        result.Error.ShouldNotBeNull().Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public void AtField_NonGenericResult_IsAttributedToo()
        => Result.Failure(Invalid).AtField("Thing").Error.ShouldBeOfType<FieldError>().Field.ShouldBe("Thing");

    [Fact]
    public void AtField_SuccessesAndOtherErrorTypes_PassThrough()
    {
        Result.Success(1).AtField("Thing").Value.ShouldBe(1);
        Result.Success().AtField("Thing").IsSuccess.ShouldBeTrue();
        Result.Failure<int>(Missing).AtField("Thing").Error.ShouldBeSameAs(Missing);
    }

    [Fact]
    public void AtField_FieldErrorOrAggregate_IsNeverReattributed()
    {
        var field = new FieldError("Other", Invalid.Code, Invalid.Description);
        var aggregate = new ValidationError([Invalid, TooLong]);

        Result.Failure<int>(field).AtField("Thing").Error.ShouldBeSameAs(field);
        Result.Failure<int>(aggregate).AtField("Thing").Error.ShouldBeSameAs(aggregate);
    }

    [Fact]
    public void AtField_WithCodes_MovesOnlyThoseCodes()
    {
        Result.Failure(TooLong).AtField("Thing", Invalid, TooLong).Error.ShouldBeOfType<FieldError>().Field.ShouldBe("Thing");
        Result.Failure(Invalid).AtField("Thing", TooLong).Error.ShouldBeSameAs(Invalid);
    }

    [Fact]
    public void AtField_EachValueObjectBeforeCombine_KeepsEveryFieldInTheAggregate()
    {
        Result combined = Result.Combine(
            Result.Failure<int>(Invalid).AtField("First"),
            Result.Failure<string>(TooLong).AtField("Second"));

        combined.Error.ShouldBeOfType<ValidationError>().Errors
            .Select(static e => e.ShouldBeOfType<FieldError>().Field)
            .ShouldBe(["First", "Second"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void AtField_WithoutAMemberName_IsAProgrammingError(string member)
    {
        Should.Throw<ArgumentException>(() => Result.Failure(Invalid).AtField(member));
        Should.Throw<ArgumentException>(() => Result.Success(1).AtField(member));
    }
}
