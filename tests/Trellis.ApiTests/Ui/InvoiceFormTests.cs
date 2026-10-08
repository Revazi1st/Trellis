#if (IncludeUI)
using System.ComponentModel.DataAnnotations;
using Shouldly;
using Trellis.Api.Contracts.Invoices;
using Trellis.UI.Common.Forms;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// The invoice forms' DataAnnotations mirror the API's validators, so the common mistakes are caught
/// before a round trip; the due-date picker's earliest day is the first one the API accepts.
/// </summary>
public sealed class InvoiceFormTests
{
    [Fact]
    public void Create_form_mirrors_the_create_validator()
    {
        var form = new CreateInvoiceForm { CustomerName = new string('n', 201), CustomerEmail = "not-an-email", Currency = "US" };

        Invalid(form).ShouldBe(
            [nameof(CreateInvoiceForm.CustomerName), nameof(CreateInvoiceForm.CustomerEmail), nameof(CreateInvoiceForm.Currency)],
            ignoreOrder: true);
        Invalid(new CreateInvoiceForm()).ShouldBe(
            [nameof(CreateInvoiceForm.CustomerName), nameof(CreateInvoiceForm.CustomerEmail)], ignoreOrder: true);
        Invalid(new CreateInvoiceForm { CustomerName = "Acme", CustomerEmail = "billing@acme.test" }).ShouldBeEmpty();
    }

    [Fact]
    public void Create_form_upper_cases_the_currency_it_sends()
        => new CreateInvoiceForm { CustomerName = "Acme", CustomerEmail = "billing@acme.test", Currency = "eur" }
            .ToRequest().ShouldBe(new CreateInvoiceRequest("Acme", "billing@acme.test", "EUR"));

    [Theory]
    [InlineData("", 1, "Description")]
    [InlineData("Consulting", -0.01, "Amount")]
    [InlineData("Consulting", 0, null)]
    public void Line_form_mirrors_the_add_line_validator(string description, double amount, string? invalidMember)
    {
        var form = new InvoiceLineForm { Description = description, Amount = (decimal)amount };
        string[] expected = invalidMember is null ? [] : [invalidMember];

        Invalid(form).ShouldBe(expected);
    }

    [Fact]
    public void Line_form_rejects_an_over_long_description()
        => Invalid(new InvoiceLineForm { Description = new string('d', 501), Amount = 1 })
            .ShouldBe([nameof(InvoiceLineForm.Description)]);

    [Fact]
    public void Issue_form_starts_two_weeks_out_and_accepts_nothing_before_tomorrow_utc()
    {
        var now = new DateTimeOffset(2026, 10, 7, 23, 30, 0, TimeSpan.FromHours(-3));   // 02:30 UTC on the 8th
        IssueInvoiceForm form = IssueInvoiceForm.At(now);

        form.Earliest.ShouldBe(new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc));
        form.EarliestValue.ShouldBe("2026-10-09");
        form.DueDate.ShouldBe(new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc));
        Invalid(form).ShouldBeEmpty();

        form.DueDate = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Unspecified);   // "today" in UTC: midnight has passed, the API would refuse it
        ValidationResult pastDue = Validate(form).ShouldHaveSingleItem();
        pastDue.MemberNames.ShouldBe([nameof(IssueInvoiceForm.DueDate)]);
        pastDue.ErrorMessage.ShouldBe("The due date cannot be in the past: pick 2026-10-09 or later.");

        form.DueDate = null;
        Validate(form).ShouldHaveSingleItem().ErrorMessage.ShouldBe("A due date is required.");
    }

    [Fact]
    public void Issue_form_sends_midnight_utc_of_the_picked_day()
    {
        IssueInvoiceForm form = IssueInvoiceForm.At(UiHarness.Start);
        form.DueDate = new DateTime(2026, 11, 1, 15, 45, 0, DateTimeKind.Local);

        form.ToRequest().DueDate.ShouldBe(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
    }

    private static List<ValidationResult> Validate(object model)
    {
        List<ValidationResult> results = [];
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }

    private static string[] Invalid(object model) => [.. Validate(model).SelectMany(static r => r.MemberNames).Distinct(StringComparer.Ordinal)];
}
#endif
