using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Trellis.Application.Features.Invoices.IssueInvoice;
using Trellis.Domain.Common;
using Trellis.Domain.Invoices;
using Xunit;

namespace Trellis.Application.UnitTests.Invoices;

public sealed class IssueInvoiceCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly IInvoiceRepository _invoices = Substitute.For<IInvoiceRepository>();
    private readonly FakeTimeProvider _time = new(Now);

    [Fact]
    public async Task Handle_FutureDueDate_IssuesTheDraft()
    {
        Invoice invoice = DraftWithLine();
        var handler = new IssueInvoiceCommandHandler(_invoices, _time);

        var result = await handler.Handle(new IssueInvoiceCommand(invoice.Id, Now.AddDays(14)), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        invoice.Status.ShouldBe(InvoiceStatus.Issued);
        invoice.DueDate.ShouldBe(Now.AddDays(14));
    }

    [Fact]
    public async Task Handle_DueDateBeforeTheClock_IsRejectedAndLeavesTheDraft()
    {
        Invoice invoice = DraftWithLine();
        var handler = new IssueInvoiceCommandHandler(_invoices, _time);

        var result = await handler.Handle(new IssueInvoiceCommand(invoice.Id, Now.AddDays(-1)), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(InvoiceErrors.DueDateInPast);
        invoice.Status.ShouldBe(InvoiceStatus.Draft);
        invoice.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_JudgesThePastByTheInjectedClock()
    {
        // Ahead of the system clock but behind the injected one: the TimeProvider decides, so tests and
        // replays are deterministic and no handler reads DateTimeOffset.UtcNow behind the clock's back.
        _time.SetUtcNow(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Invoice invoice = DraftWithLine();
        var handler = new IssueInvoiceCommandHandler(_invoices, _time);

        var result = await handler.Handle(
            new IssueInvoiceCommand(invoice.Id, new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            TestContext.Current.CancellationToken);

        result.Error.ShouldBe(InvoiceErrors.DueDateInPast);
    }

    [Fact]
    public async Task Handle_UnknownInvoice_ReturnsNotFound()
    {
        var missingId = InvoiceId.New();
        _invoices.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).Returns((Invoice?)null);
        var handler = new IssueInvoiceCommandHandler(_invoices, _time);

        var result = await handler.Handle(new IssueInvoiceCommand(missingId, Now.AddDays(14)), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(InvoiceErrors.NotFound(missingId));
    }

    [Fact]
    public void Validator_MissingDueDate_IsRejected()
    {
        // A body without "dueDate" binds to default(DateTimeOffset): the validator answers it before the handler runs.
        var result = new IssueInvoiceCommandValidator().Validate(new IssueInvoiceCommand(InvoiceId.New(), default));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe(nameof(IssueInvoiceCommand.DueDate));
    }

    [Fact]
    public void Validator_AnyDueDate_IsShapeValid()
        // Shape only: "not in the past" needs the clock and is the aggregate's rule.
        => new IssueInvoiceCommandValidator().Validate(new IssueInvoiceCommand(InvoiceId.New(), Now.AddYears(-1)))
            .IsValid.ShouldBeTrue();

    private Invoice DraftWithLine()
    {
        Invoice invoice = InvoiceTestData.Draft();
        invoice.AddLine("Consulting", Money.Create(100m, "USD").Value);
        _invoices.GetByIdAsync(invoice.Id, Arg.Any<CancellationToken>()).Returns(invoice);
        return invoice;
    }
}
