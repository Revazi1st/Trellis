using Shouldly;
using Trellis.Domain.Common;
using Xunit;

namespace Trellis.Domain.UnitTests.Common;

public sealed class EmailAddressTests
{
    [Theory]
    [InlineData("user@example.com")]
    [InlineData("  padded@example.io  ")]
    public void Create_accepts_valid_shapes(string input)
    {
        var email = EmailAddress.Create(input);

        email.IsSuccess.ShouldBeTrue();
        email.Value.Value.ShouldBe(input.Trim());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty(string? input)
        => EmailAddress.Create(input).Error.ShouldBe(EmailAddressErrors.Empty);

    [Theory]
    [InlineData("no-at-sign.com")]
    [InlineData("@leading.com")]
    [InlineData("trailing@")]
    [InlineData("two@@signs.com")]
    [InlineData("no@dot")]
    [InlineData("spa ce@example.com")]
    public void Create_rejects_invalid_shapes(string input)
        => EmailAddress.Create(input).Error.ShouldBe(EmailAddressErrors.Invalid);
}
