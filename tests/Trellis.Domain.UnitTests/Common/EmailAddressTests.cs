using Shouldly;
using Trellis.Domain.Common;
using Xunit;

namespace Trellis.Domain.UnitTests.Common;

public sealed class EmailAddressTests
{
    [Theory]
    [InlineData("user@example.com")]
    [InlineData("  padded@example.io  ")]
    [InlineData("first.last+tag@sub.example.co.uk")]
    [InlineData("a-b@my-domain.io")]
    [InlineData("dörte@exämple.de")]          // internationalized addresses are accepted as typed
    [InlineData("用户@例子.广告")]
    public void Create_accepts_valid_shapes(string input)
    {
        var email = EmailAddress.Create(input);

        email.ShouldBeSuccess();
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
    [InlineData("tab\tinside@example.com")]
    [InlineData("user..name@example.com")]   // doubled dot in the local part
    [InlineData(".user@example.com")]        // leading dot
    [InlineData("user.@example.com")]        // trailing dot
    [InlineData("user@example..com")]        // empty domain label
    [InlineData("user@.example.com")]
    [InlineData("user@example.com.")]
    [InlineData("user@-example.com")]        // a label never starts or ends with a hyphen
    [InlineData("user@example-.com")]
    [InlineData("user@sub.-example.com")]
    public void Create_rejects_invalid_shapes(string input)
        => EmailAddress.Create(input).Error.ShouldBe(EmailAddressErrors.Invalid);

    [Fact]
    public void Create_accepts_the_longest_address_and_local_part()
    {
        string localPart = new('l', EmailAddress.MaxLocalPartLength);
        string domain = new string('d', EmailAddress.MaxLength - localPart.Length - "@.io".Length) + ".io";
        string longest = $"{localPart}@{domain}";

        longest.Length.ShouldBe(EmailAddress.MaxLength);
        EmailAddress.Create(longest).ShouldBeSuccess();
    }

    [Fact]
    public void Create_rejects_an_address_over_254_characters()
    {
        string tooLong = new string('l', 10) + "@" + new string('d', EmailAddress.MaxLength - 10 - "@.io".Length + 1) + ".io";

        tooLong.Length.ShouldBe(EmailAddress.MaxLength + 1);
        EmailAddress.Create(tooLong).Error.ShouldBe(EmailAddressErrors.TooLong);
    }

    [Fact]
    public void Create_rejects_a_local_part_over_64_characters()
        => EmailAddress.Create(new string('l', EmailAddress.MaxLocalPartLength + 1) + "@example.com")
            .Error.ShouldBe(EmailAddressErrors.TooLong);
}
