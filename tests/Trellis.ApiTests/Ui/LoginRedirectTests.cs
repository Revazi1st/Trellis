#if (IncludeUI)
using Shouldly;
using Trellis.UI.Common.Auth;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// The sign-in round trip carries the page to return to, and the sign-in page honours it only when it is
/// a plain path inside the app: the query string is anyone's to write, so it must not be an open redirect.
/// </summary>
public sealed class LoginRedirectTests
{
    [Theory]
    [InlineData("invoices")]
    [InlineData("invoices/0199b0a0-0000-7000-8000-000000000001")]
    [InlineData("invoices?page=2")]
    [InlineData("invoices/new#lines")]
    [InlineData("invoices/a%20b")]
    public void A_page_of_this_app_is_honoured(string returnUrl)
        => LoginRedirect.LocalReturnUrl(returnUrl).ShouldBe(returnUrl);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/invoices")]
    [InlineData("/invoices")]
    [InlineData("\\\\evil.example")]
    [InlineData("invoices\\..\\..\\x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("../admin")]
    [InlineData("invoices/../../admin")]
    [InlineData("%2e%2e/admin")]
    [InlineData("invoices/%2E%2E/%2e%2E/admin")]
    [InlineData(" //evil.example")]
    [InlineData("\t//evil.example")]
    [InlineData("java\nscript:alert(1)")]
    [InlineData("invoices with spaces")]
    public void Anything_that_could_leave_the_app_is_refused(string? returnUrl)
        => LoginRedirect.LocalReturnUrl(returnUrl).ShouldBeNull();

    [Fact]
    public void An_over_long_return_url_is_refused()
        => LoginRedirect.LocalReturnUrl("invoices/" + new string('a', 2048)).ShouldBeNull();

    [Theory]
    [InlineData("invoices/42", "login?returnUrl=invoices%2F42")]
    [InlineData("invoices?page=2", "login?returnUrl=invoices%3Fpage%3D2")]
    [InlineData("", "login")]
    [InlineData("//evil.example", "login")]
    public void The_sign_in_address_carries_only_a_local_page(string from, string expected)
        => LoginRedirect.From(from).ShouldBe(expected);
}
#endif
