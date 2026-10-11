using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Trellis.Application.Features.Invoices.IssueInvoice;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Results;
using Trellis.TestSupport.Invoices;
using Xunit;

namespace Trellis.Application.UnitTests.Features.Invoices.IssueInvoice;

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

        result.ShouldBeSuccess();
        invoice.Status.ShouldBe(InvoiceStatus.Issued);
        invoice.DueDate.ShouldBe(Now.AddDays(14));
    }

    [Fact]
    public async Task Handle_DueDateBeforeTheClock_IsRejectedAndLeavesTheDraft()
    {
        Invoice invoice = DraftWithLine();
        var handler = new IssueInvoiceCommandHandler(_invoices, _time);

        var result = await handler.Handle(new IssueInvoiceCommand(invoice.Id, Now.AddDays(-1)), TestContext.Current.CancellationToken);

        FieldError error = result.Error.ShouldBeOfType<FieldError>();
        error.Field.ShouldBe(nameof(IssueInvoiceCommand.DueDate));
        error.Code.ShouldBe(InvoiceErrors.DueDateInPast.Code);
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

        result.Error.ShouldNotBeNull().Code.ShouldBe(InvoiceErrors.DueDateInPast.Code);
    }

    [Fact]
    public async Task Handle_DraftWithoutLines_IsAWholeRequestError()
    {
        // Issue has several rules; only the due date's belongs to one request member.
        Invoice invoice = InvoiceFactory.Draft();
        _invoices.GetByIdAsync(invoice.Id, Arg.Any<CancellationToken>()).Returns(invoice);
        var handler = new IssueInvoiceCommandHandler(_invoices, _time);

        var result = await handler.Handle(new IssueInvoiceCommand(invoice.Id, Now.AddDays(-1)), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(InvoiceErrors.NoLines);
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

    private Invoice DraftWithLine()
    {
        Invoice invoice = InvoiceFactory.DraftWithLine();
        _invoices.GetByIdAsync(invoice.Id, Arg.Any<CancellationToken>()).Returns(invoice);
        return invoice;
    }
}
