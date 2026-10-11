using Microsoft.Extensions.DependencyInjection;
using Trellis.Application.Features.Invoices.AddInvoiceLine;
using Trellis.Application.Features.Invoices.CreateInvoice;
using Trellis.Application.Features.Invoices.IssueInvoice;
using Trellis.Application.Features.Invoices.PayInvoice;
using Trellis.Application.Features.Invoices.VoidInvoice;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;
using Trellis.TestSupport.Invoices;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// The Invoice use cases as one-line arrange steps. Each resolves the real handler from the container, so
/// the command runs through the whole decorator pipeline (validation, the unit of work, the outbox write),
/// sends a valid command built from <see cref="InvoiceFactory"/>'s defaults, fails naming the error if the
/// use case did, and returns the invoice's id. Call it on a test's <c>Services</c> or on a fresh scope's
/// <c>ServiceProvider</c> (a separate unit of work). Arrange only: a test about a use case's outcome
/// calls the handler and asserts on its <c>Result</c>. One method per use case, deliberately, and no
/// generic Send: the call sites stay readable and nothing grows back into a mediator.
/// The paired HTTP steps are ApiTests' <c>InvoiceHttpDriver</c>.
/// </summary>
internal static class InvoiceDriver
{
    public static async Task<InvoiceId> CreateInvoiceAsync(
        this IServiceProvider services,
        string customerName = InvoiceFactory.Customer,
        string customerEmail = InvoiceFactory.Email,
        string currency = InvoiceFactory.CurrencyCode)
        => (await services.GetRequiredService<ICommandHandler<CreateInvoiceCommand, InvoiceId>>()
            .Handle(InvoiceFactory.CreateCommand(customerName, customerEmail, currency), TestContext.Current.CancellationToken))
            .ShouldBeSuccess();

    public static async Task<InvoiceId> AddInvoiceLineAsync(
        this IServiceProvider services,
        InvoiceId invoiceId,
        string description = InvoiceFactory.LineDescription,
        decimal amount = InvoiceFactory.LineAmount)
    {
        (await services.GetRequiredService<ICommandHandler<AddInvoiceLineCommand>>()
            .Handle(InvoiceFactory.AddLineCommand(invoiceId, description, amount), TestContext.Current.CancellationToken))
            .ShouldBeSuccess();
        return invoiceId;
    }

    /// <summary>Without a <paramref name="dueDate"/>, due 14 days after the container's clock (a due date is never past when issued).</summary>
    public static async Task<InvoiceId> IssueInvoiceAsync(this IServiceProvider services, InvoiceId invoiceId, DateTimeOffset? dueDate = null)
    {
        DateTimeOffset due = dueDate ?? services.GetRequiredService<TimeProvider>().GetUtcNow().AddDays(14);
        (await services.GetRequiredService<ICommandHandler<IssueInvoiceCommand>>()
            .Handle(InvoiceFactory.IssueCommand(invoiceId, due), TestContext.Current.CancellationToken))
            .ShouldBeSuccess();
        return invoiceId;
    }

    public static async Task<InvoiceId> PayInvoiceAsync(this IServiceProvider services, InvoiceId invoiceId)
    {
        (await services.GetRequiredService<ICommandHandler<PayInvoiceCommand>>()
            .Handle(new PayInvoiceCommand(invoiceId), TestContext.Current.CancellationToken))
            .ShouldBeSuccess();
        return invoiceId;
    }

    public static async Task<InvoiceId> VoidInvoiceAsync(this IServiceProvider services, InvoiceId invoiceId)
    {
        (await services.GetRequiredService<ICommandHandler<VoidInvoiceCommand>>()
            .Handle(new VoidInvoiceCommand(invoiceId), TestContext.Current.CancellationToken))
            .ShouldBeSuccess();
        return invoiceId;
    }

    /// <summary>A draft with one line, issued: the state most outbox and listing tests start from.</summary>
    public static async Task<InvoiceId> IssuedInvoiceAsync(
        this IServiceProvider services, DateTimeOffset? dueDate = null, string customerName = InvoiceFactory.Customer)
    {
        InvoiceId id = await services.CreateInvoiceAsync(customerName);
        await services.AddInvoiceLineAsync(id);
        return await services.IssueInvoiceAsync(id, dueDate);
    }
}
