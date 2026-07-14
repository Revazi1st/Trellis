using Shouldly;
using XTemplate.Domain.Common;
using XTemplate.Domain.Invoices;
using XTemplate.Domain.Invoices.Events;
using Xunit;

namespace XTemplate.Domain.UnitTests.Invoices;

public sealed class InvoiceTests
{
    private static readonly DateTimeOffset DueDate = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PaidAt = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static Invoice CreateDraft()
        => Invoice.Create("ACME Corp", EmailAddress.Create("billing@acme.com").Value, "USD").Value;

    private static Invoice CreateIssued()
    {
        Invoice invoice = CreateDraft();
        invoice.AddLine("Consulting", Money.Create(100m, "USD").Value);
        invoice.Issue(DueDate);
        return invoice;
    }

    [Fact]
    public void Create_starts_as_empty_draft()
    {
        Invoice invoice = CreateDraft();

        invoice.Status.ShouldBe(InvoiceStatus.Draft);
        invoice.Lines.ShouldBeEmpty();
        invoice.Id.Value.ShouldNotBe(Guid.Empty);
        invoice.Total.ShouldBe(Money.Zero("USD"));
    }

    [Fact]
    public void Create_rejects_blank_customer_name()
        => Invoice.Create("  ", EmailAddress.Create("a@b.co").Value, "USD")
            .Error.ShouldBe(InvoiceErrors.EmptyCustomerName);

    [Fact]
    public void Create_rejects_invalid_currency()
        => Invoice.Create("ACME", EmailAddress.Create("a@b.co").Value, "X")
            .Error.ShouldBe(MoneyErrors.InvalidCurrency);

    [Fact]
    public void AddLine_accumulates_total()
    {
        Invoice invoice = CreateDraft();

        invoice.AddLine("Design", Money.Create(150m, "USD").Value).IsSuccess.ShouldBeTrue();
        invoice.AddLine("Build", Money.Create(350.25m, "USD").Value).IsSuccess.ShouldBeTrue();

        invoice.Lines.Count.ShouldBe(2);
        invoice.Total.Amount.ShouldBe(500.25m);
    }

    [Fact]
    public void AddLine_rejects_currency_mismatch()
        => CreateDraft().AddLine("Design", Money.Create(1m, "EUR").Value)
            .Error.ShouldBe(InvoiceErrors.CurrencyMismatch);

    [Fact]
    public void AddLine_rejects_blank_description()
        => CreateDraft().AddLine(" ", Money.Create(1m, "USD").Value)
            .Error.ShouldBe(InvoiceErrors.EmptyLineDescription);

    [Fact]
    public void AddLine_after_issue_is_rejected()
        => CreateIssued().AddLine("Late line", Money.Create(1m, "USD").Value)
            .Error.ShouldBe(InvoiceErrors.NotDraft);

    [Fact]
    public void Issue_requires_at_least_one_line()
        => CreateDraft().Issue(DueDate).Error.ShouldBe(InvoiceErrors.NoLines);

    [Fact]
    public void Issue_sets_status_due_date_and_raises_event()
    {
        Invoice invoice = CreateDraft();
        invoice.AddLine("Consulting", Money.Create(100m, "USD").Value);

        invoice.Issue(DueDate).IsSuccess.ShouldBeTrue();

        invoice.Status.ShouldBe(InvoiceStatus.Issued);
        invoice.DueDate.ShouldBe(DueDate);
        invoice.DomainEvents.ShouldHaveSingleItem()
            .ShouldBeOfType<InvoiceIssuedDomainEvent>()
            .InvoiceId.ShouldBe(invoice.Id);
    }

    [Fact]
    public void Issue_twice_is_rejected()
        => CreateIssued().Issue(DueDate).Error.ShouldBe(InvoiceErrors.AlreadyFinalized);

    [Fact]
    public void MarkPaid_requires_issued_status()
        => CreateDraft().MarkPaid(PaidAt).Error.ShouldBe(InvoiceErrors.NotIssued);

    [Fact]
    public void MarkPaid_records_time_and_raises_event_with_email()
    {
        Invoice invoice = CreateIssued();
        invoice.ClearDomainEvents();

        invoice.MarkPaid(PaidAt).IsSuccess.ShouldBeTrue();

        invoice.Status.ShouldBe(InvoiceStatus.Paid);
        invoice.PaidAtUtc.ShouldBe(PaidAt);
        var paidEvent = invoice.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<InvoicePaidDomainEvent>();
        paidEvent.CustomerEmail.Value.ShouldBe("billing@acme.com");
    }

    [Fact]
    public void MarkPaid_twice_is_rejected()
    {
        Invoice invoice = CreateIssued();
        invoice.MarkPaid(PaidAt);

        invoice.MarkPaid(PaidAt).Error.ShouldBe(InvoiceErrors.NotIssued);
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.Issued)]
    public void Void_allowed_before_payment(InvoiceStatus from)
    {
        Invoice invoice = from == InvoiceStatus.Draft ? CreateDraft() : CreateIssued();

        invoice.Void().IsSuccess.ShouldBeTrue();
        invoice.Status.ShouldBe(InvoiceStatus.Voided);
    }

    [Fact]
    public void Void_after_payment_is_rejected()
    {
        Invoice invoice = CreateIssued();
        invoice.MarkPaid(PaidAt);

        invoice.Void().Error.ShouldBe(InvoiceErrors.AlreadyFinalized);
    }

    [Fact]
    public void Overdue_spec_matches_only_issued_past_due()
    {
        Invoice overdue = CreateIssued();                       // due 2026-08-01
        Invoice paid = CreateIssued();
        paid.MarkPaid(PaidAt);
        Invoice draft = CreateDraft();

        var spec = InvoiceSpecs.Overdue(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        spec.Evaluate([overdue, paid, draft]).ShouldBe([overdue]);
    }
}
