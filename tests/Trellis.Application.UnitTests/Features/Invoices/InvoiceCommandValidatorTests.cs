using FluentValidation.Results;
using Shouldly;
using Trellis.Application.Features.Invoices.AddInvoiceLine;
using Trellis.Application.Features.Invoices.CreateInvoice;
using Trellis.Application.Features.Invoices.IssueInvoice;
using Trellis.Application.Features.Invoices.ListOverdueInvoices;
using Trellis.Domain.Common;
using Trellis.Domain.Invoices;
using Trellis.TestSupport.Invoices;
using Xunit;

namespace Trellis.Application.UnitTests.Features.Invoices;

/// <summary>
/// The bounds of every Invoice validator, at the boundary: the largest value accepted, one more rejected
/// naming its member. The lengths come from the Domain constants the validator, the column, the aggregate
/// and the UI form share, so a constant that moves without its rule (or the reverse) fails here.
/// </summary>
public sealed class InvoiceCommandValidatorTests
{
    private static readonly InvoiceId AnyInvoice = InvoiceId.New();

    [Fact]
    public void CreateInvoice_customer_name_is_bounded_by_the_aggregate()
    {
        Validate(InvoiceFactory.CreateCommand(customerName: new string('n', Invoice.CustomerNameMaxLength))).IsValid.ShouldBeTrue();

        Validate(InvoiceFactory.CreateCommand(customerName: new string('n', Invoice.CustomerNameMaxLength + 1)))
            .ShouldBeRejectedAt(nameof(CreateInvoiceCommand.CustomerName));
    }

    [Fact]
    public void CreateInvoice_customer_email_is_bounded_by_the_value_object()
    {
        Validate(InvoiceFactory.CreateCommand(customerEmail: EmailOfLength(EmailAddress.MaxLength))).IsValid.ShouldBeTrue();

        Validate(InvoiceFactory.CreateCommand(customerEmail: EmailOfLength(EmailAddress.MaxLength + 1)))
            .ShouldBeRejectedAt(nameof(CreateInvoiceCommand.CustomerEmail));
    }

    [Fact]
    public void CreateInvoice_currency_is_exactly_as_long_as_an_ISO_code()
    {
        Validate(InvoiceFactory.CreateCommand(currency: new string('U', Currency.CodeLength))).IsValid.ShouldBeTrue();

        Validate(InvoiceFactory.CreateCommand(currency: new string('U', Currency.CodeLength - 1)))
            .ShouldBeRejectedAt(nameof(CreateInvoiceCommand.Currency));
        Validate(InvoiceFactory.CreateCommand(currency: new string('U', Currency.CodeLength + 1)))
            .ShouldBeRejectedAt(nameof(CreateInvoiceCommand.Currency));
    }

    [Fact]
    public void AddInvoiceLine_description_is_bounded_by_the_line()
    {
        Validate(InvoiceFactory.AddLineCommand(AnyInvoice, description: new string('d', InvoiceLine.DescriptionMaxLength))).IsValid.ShouldBeTrue();

        Validate(InvoiceFactory.AddLineCommand(AnyInvoice, description: new string('d', InvoiceLine.DescriptionMaxLength + 1)))
            .ShouldBeRejectedAt(nameof(AddInvoiceLineCommand.Description));
    }

    [Fact]
    public void AddInvoiceLine_amount_may_be_zero_but_never_negative()
    {
        Validate(InvoiceFactory.AddLineCommand(AnyInvoice, amount: 0m)).IsValid.ShouldBeTrue();

        Validate(InvoiceFactory.AddLineCommand(AnyInvoice, amount: -0.01m)).ShouldBeRejectedAt(nameof(AddInvoiceLineCommand.Amount));
    }

    [Fact]
    public void IssueInvoice_requires_a_due_date_but_leaves_the_past_to_the_aggregate()
    {
        // A body without "dueDate" binds to default(DateTimeOffset): the validator answers it before the handler
        // runs. "Not in the past" needs the clock, so any real date passes here (Invoice.Issue judges it).
        Validate(InvoiceFactory.IssueCommand(AnyInvoice, default)).ShouldBeRejectedAt(nameof(IssueInvoiceCommand.DueDate));

        Validate(InvoiceFactory.IssueCommand(AnyInvoice, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero))).IsValid.ShouldBeTrue();
    }

    // The bounds the endpoint declares (Api.Contracts' PagingLimits, out of Application's reach), held here.
    [Theory]
    [InlineData(1, 1)]
    [InlineData(10_000, 100)]
    public void ListOverdueInvoices_accepts_paging_at_its_bounds(int page, int pageSize)
        => Validate(new ListOverdueInvoicesQuery(page, pageSize)).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData(0, 20, nameof(ListOverdueInvoicesQuery.Page))]
    [InlineData(10_001, 20, nameof(ListOverdueInvoicesQuery.Page))]
    [InlineData(1, 0, nameof(ListOverdueInvoicesQuery.PageSize))]
    [InlineData(1, 101, nameof(ListOverdueInvoicesQuery.PageSize))]
    public void ListOverdueInvoices_rejects_paging_past_its_bounds_naming_the_member(int page, int pageSize, string member)
        => Validate(new ListOverdueInvoicesQuery(page, pageSize)).ShouldBeRejectedAt(member);

    private static string EmailOfLength(int length) => new string('a', length - "@acme.com".Length) + "@acme.com";

    private static ValidationResult Validate(CreateInvoiceCommand command) => new CreateInvoiceCommandValidator().Validate(command);

    private static ValidationResult Validate(AddInvoiceLineCommand command) => new AddInvoiceLineCommandValidator().Validate(command);

    private static ValidationResult Validate(IssueInvoiceCommand command) => new IssueInvoiceCommandValidator().Validate(command);

    private static ValidationResult Validate(ListOverdueInvoicesQuery query) => new ListOverdueInvoicesQueryValidator().Validate(query);
}

internal static class ValidationResultAssertions
{
    /// <summary>The only failure is on <paramref name="property"/>: the rule under test fired, and no other.</summary>
    public static void ShouldBeRejectedAt(this ValidationResult result, string property)
    {
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe(property);
    }
}
