using Shouldly;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.Domain.UnitTests.SharedKernel;

/// <summary>
/// The shared <c>ShouldBeSuccess()</c> (tests/TestSupport) is only worth its place if a failure explains
/// itself: these pin what its message carries.
/// </summary>
public sealed class ResultAssertionsTests
{
    [Fact]
    public void ShouldBeSuccess_passes_a_success_and_returns_its_value()
    {
        Result.Success().ShouldBeSuccess();
        Result.Success(42).ShouldBeSuccess().ShouldBe(42);
    }

    [Fact]
    public void ShouldBeSuccess_on_a_failure_names_the_expression_code_type_and_description()
    {
        Result notDraft = Error.Conflict("Invoice.NotDraft", "Lines can only be changed while the invoice is a draft.");

        string message = Should.Throw<ShouldAssertException>(() => notDraft.ShouldBeSuccess()).Message;

        message.ShouldStartWith("notDraft");
        message.ShouldContain("Invoice.NotDraft (Conflict): Lines can only be changed while the invoice is a draft.");
    }

    [Fact]
    public void ShouldBeSuccess_on_a_validation_failure_lists_every_error_it_carries()
    {
        Result<int> rejected = new ValidationError(
        [
            new FieldError("CustomerEmail", "EmailAddress.Invalid", "The e-mail address is not valid."),
            Error.Validation("Request.Inconsistent", "The request contradicts itself."),
        ]);

        string message = Should.Throw<ShouldAssertException>(() => rejected.ShouldBeSuccess("while seeding")).Message;

        message.ShouldContain("Validation.General (Validation)");
        message.ShouldContain("CustomerEmail: EmailAddress.Invalid (Validation): The e-mail address is not valid.");
        message.ShouldContain("Request.Inconsistent (Validation): The request contradicts itself.");
        message.ShouldContain("while seeding");
    }
}
