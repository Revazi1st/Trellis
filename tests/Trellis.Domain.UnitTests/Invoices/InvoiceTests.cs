using Shouldly;
using Trellis.Domain.Common;
using Trellis.Domain.Invoices;
using Trellis.Domain.Invoices.Events;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.Domain.UnitTests.Invoices;

public sealed class InvoiceTests
{
    /// <summary>The clock handed to Issue: the moment of issuing.</summary>
    private static readonly DateTimeOffset IssuedAt = new(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DueDate = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PaidAt = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static Invoice CreateDraft()
        => Invoice.Create("ACME Corp", EmailAddress.Create("billing@acme.com").Value, Currency.Create("USD").Value).Value;

    private static Invoice CreateDraftWithLine()
    {
        Invoice invoice = CreateDraft();
        invoice.AddLine("Consulting", Money.Create(100m, "USD").Value);
        return invoice;
    }

    private static Invoice CreateIssued()
    {
        Invoice invoice = CreateDraftWithLine();
        invoice.Issue(DueDate, IssuedAt);
        return invoice;
    }

    private static Invoice CreatePaid()
    {
        Invoice invoice = CreateIssued();
        invoice.MarkPaid(PaidAt);
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
        => Invoice.Create("  ", EmailAddress.Create("a@b.co").Value, Currency.Create("USD").Value)
            .Error.ShouldBe(InvoiceErrors.EmptyCustomerName);

    [Fact]
    public void Create_bounds_the_customer_name_after_trimming_it()
    {
        EmailAddress email = EmailAddress.Create("a@b.co").Value;
        Currency usd = Currency.Create("USD").Value;
        string longest = new('n', Invoice.CustomerNameMaxLength);

        Invoice.Create($"  {longest}  ", email, usd).Value.CustomerName.ShouldBe(longest);
        Invoice.Create(longest + "n", email, usd).Error.ShouldBe(InvoiceErrors.CustomerNameTooLong);
    }

    [Fact]
    public void AddLine_bounds_the_description_after_trimming_it()
    {
        Invoice invoice = CreateDraft();
        string longest = new('d', InvoiceLine.DescriptionMaxLength);

        invoice.AddLine($" {longest} ", Money.Create(1m, "USD").Value).IsSuccess.ShouldBeTrue();
        invoice.Lines.ShouldHaveSingleItem().Description.ShouldBe(longest);
        invoice.AddLine(longest + "d", Money.Create(1m, "USD").Value).Error.ShouldBe(InvoiceErrors.LineDescriptionTooLong);
        invoice.Lines.Count.ShouldBe(1);
    }

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
        => CreateDraft().Issue(DueDate, IssuedAt).Error.ShouldBe(InvoiceErrors.NoLines);

    [Fact]
    public void Issue_sets_status_due_date_and_raises_event()
    {
        Invoice invoice = CreateDraftWithLine();

        invoice.Issue(DueDate, IssuedAt).IsSuccess.ShouldBeTrue();

        invoice.Status.ShouldBe(InvoiceStatus.Issued);
        invoice.DueDate.ShouldBe(DueDate);
        invoice.DomainEvents.ShouldHaveSingleItem()
            .ShouldBeOfType<InvoiceIssuedDomainEvent>()
            .InvoiceId.ShouldBe(invoice.Id);
    }

    [Theory]
    [InlineData(-1L)]                   // a tick before now
    [InlineData(-TimeSpan.TicksPerDay)] // yesterday
    public void Issue_rejects_a_due_date_in_the_past(long ticksFromNow)
    {
        Invoice invoice = CreateDraftWithLine();

        invoice.Issue(IssuedAt.AddTicks(ticksFromNow), IssuedAt).Error.ShouldBe(InvoiceErrors.DueDateInPast);

        invoice.Status.ShouldBe(InvoiceStatus.Draft);
        invoice.DueDate.ShouldBeNull();
        invoice.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Issue_rejects_the_default_due_date_whatever_the_clock_says()
    {
        // Explicit, not a side effect of "before now": a missing date is never a valid one.
        CreateDraftWithLine().Issue(default, IssuedAt).Error.ShouldBe(InvoiceErrors.DueDateInPast);
        CreateDraftWithLine().Issue(default, DateTimeOffset.MinValue).Error.ShouldBe(InvoiceErrors.DueDateInPast);
    }

    [Fact]
    public void Issue_accepts_a_due_date_equal_to_now()
        => CreateDraftWithLine().Issue(IssuedAt, IssuedAt).IsSuccess.ShouldBeTrue();

    [Fact]
    public void Issue_compares_instants_not_offsets()
    {
        // 10:00 at +02:00 is 08:00 UTC: an hour before IssuedAt (09:00 UTC), so in the past.
        var pastInAnotherZone = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.FromHours(2));

        CreateDraftWithLine().Issue(pastInAnotherZone, IssuedAt).Error.ShouldBe(InvoiceErrors.DueDateInPast);
    }

    [Fact]
    public void Issue_twice_is_rejected_as_already_issued()
        => CreateIssued().Issue(DueDate, IssuedAt).Error.ShouldBe(InvoiceErrors.AlreadyIssued);

    [Fact]
    public void Issue_on_a_paid_invoice_is_rejected_as_finalized()
        => CreatePaid().Issue(DueDate, IssuedAt).Error.ShouldBe(InvoiceErrors.AlreadyFinalized);

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
        paidEvent.CustomerEmail.ShouldBe("billing@acme.com");
    }

    [Fact]
    public void MarkPaid_twice_is_rejected()
        => CreatePaid().MarkPaid(PaidAt).Error.ShouldBe(InvoiceErrors.NotIssued);

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.Issued)]
    public void Void_allowed_before_payment_and_raises_event(InvoiceStatus from)
    {
        Invoice invoice = from == InvoiceStatus.Draft ? CreateDraft() : CreateIssued();
        invoice.ClearDomainEvents();

        invoice.Void().IsSuccess.ShouldBeTrue();

        invoice.Status.ShouldBe(InvoiceStatus.Voided);
        invoice.DomainEvents.ShouldHaveSingleItem()
            .ShouldBeOfType<InvoiceVoidedDomainEvent>()
            .InvoiceId.ShouldBe(invoice.Id);
    }

    [Fact]
    public void Void_after_payment_is_rejected()
        => CreatePaid().Void().Error.ShouldBe(InvoiceErrors.AlreadyFinalized);

    [Fact]
    public void Void_twice_is_rejected()
    {
        Invoice invoice = CreateIssued();
        invoice.Void();

        invoice.Void().Error.ShouldBe(InvoiceErrors.AlreadyFinalized);
    }

    public static TheoryData<InvoiceStatus, string> TransitionsOnAVoidedInvoice()
    {
        var data = new TheoryData<InvoiceStatus, string>();
        foreach (InvoiceStatus voidedFrom in (InvoiceStatus[])[InvoiceStatus.Draft, InvoiceStatus.Issued])
        {
            foreach (string transition in (string[])[nameof(Invoice.AddLine), nameof(Invoice.Issue), nameof(Invoice.MarkPaid), nameof(Invoice.Void)])
            {
                data.Add(voidedFrom, transition);
            }
        }

        return data;
    }

    /// <summary>Voided is terminal: whichever state it was reached from, nothing moves it again.</summary>
    [Theory]
    [MemberData(nameof(TransitionsOnAVoidedInvoice))]
    public void Every_transition_on_a_voided_invoice_is_rejected(InvoiceStatus voidedFrom, string transition)
    {
        // A line is added up front so a rejected Issue is about the state, never about NoLines.
        Invoice invoice = voidedFrom == InvoiceStatus.Draft ? CreateDraftWithLine() : CreateIssued();
        invoice.Void().IsSuccess.ShouldBeTrue();
        invoice.ClearDomainEvents();

        Result result = Attempt(invoice, transition);

        result.Error.ShouldBe(transition switch
        {
            nameof(Invoice.AddLine) => InvoiceErrors.NotDraft,
            nameof(Invoice.MarkPaid) => InvoiceErrors.NotIssued,
            _ => InvoiceErrors.AlreadyFinalized,
        });
        invoice.Status.ShouldBe(InvoiceStatus.Voided);
        invoice.DomainEvents.ShouldBeEmpty();
    }

    /// <summary>A rejected transition changes nothing: no state, and above all no event for the outbox to deliver.</summary>
    [Theory]
    [InlineData("issue a draft without lines")]
    [InlineData("issue with a past due date")]
    [InlineData("issue an issued invoice")]
    [InlineData("add a line to an issued invoice")]
    [InlineData("pay a draft")]
    [InlineData("pay a paid invoice")]
    [InlineData("void a paid invoice")]
    [InlineData("void a voided invoice")]
    public void Failed_transitions_raise_no_domain_events(string scenario)
    {
        (Invoice Invoice, Func<Invoice, Result> Transition) setup = scenario switch
        {
            "issue a draft without lines" => (CreateDraft(), static i => i.Issue(DueDate, IssuedAt)),
            "issue with a past due date" => (CreateDraftWithLine(), static i => i.Issue(IssuedAt.AddDays(-1), IssuedAt)),
            "issue an issued invoice" => (CreateIssued(), static i => i.Issue(DueDate, IssuedAt)),
            "add a line to an issued invoice" => (CreateIssued(), static i => i.AddLine("Late", Money.Create(1m, "USD").Value)),
            "pay a draft" => (CreateDraftWithLine(), static i => i.MarkPaid(PaidAt)),
            "pay a paid invoice" => (CreatePaid(), static i => i.MarkPaid(PaidAt)),
            "void a paid invoice" => (CreatePaid(), static i => i.Void()),
            "void a voided invoice" => (VoidedDraft(), static i => i.Void()),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario."),
        };
        Invoice invoice = setup.Invoice;
        invoice.ClearDomainEvents();
        InvoiceStatus before = invoice.Status;

        setup.Transition(invoice).IsFailure.ShouldBeTrue();

        invoice.DomainEvents.ShouldBeEmpty();
        invoice.Status.ShouldBe(before);

        static Invoice VoidedDraft()
        {
            Invoice voided = CreateDraft();
            voided.Void();
            return voided;
        }
    }

    [Fact]
    public void Overdue_spec_matches_only_issued_past_due()
    {
        Invoice overdue = CreateIssued();                       // due 2026-08-01
        Invoice paid = CreatePaid();
        Invoice draft = CreateDraft();

        var spec = InvoiceSpecs.Overdue(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        spec.Evaluate([overdue, paid, draft]).ShouldBe([overdue]);
    }

    private static Result Attempt(Invoice invoice, string transition) => transition switch
    {
        nameof(Invoice.AddLine) => invoice.AddLine("Late line", Money.Create(1m, "USD").Value),
        nameof(Invoice.Issue) => invoice.Issue(DueDate, IssuedAt),
        nameof(Invoice.MarkPaid) => invoice.MarkPaid(PaidAt),
        nameof(Invoice.Void) => invoice.Void(),
        _ => throw new ArgumentOutOfRangeException(nameof(transition), transition, "Unknown transition."),
    };
}
