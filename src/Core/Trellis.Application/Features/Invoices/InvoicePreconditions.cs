using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Results;

namespace Trellis.Application.Features.Invoices;

/// <summary>
/// The optional version precondition the writes to an existing invoice carry (at the API, an
/// <c>If-Match</c> naming the <c>ETag</c> the caller read; ADR-0025). The aggregate's <c>Version</c> is
/// the row version the whole aggregate shares, so it moves with every change, a line's included; a
/// write that names an older one would act on a state the caller never saw (issuing lines a colleague
/// just added, voiding a draft someone just edited). The commit's own concurrency check still guards
/// the window between this comparison and the commit.
/// </summary>
public static class InvoicePreconditions
{
    /// <summary>The caller expected a version the invoice has moved on from; nothing was changed.</summary>
    public static readonly Error VersionMismatch = Error.PreconditionFailed(
        "Invoice.VersionMismatch",
        "The invoice has changed since the version you expected. Reload it and try again.");

    /// <summary>
    /// The loaded invoice a write may act on: <see cref="InvoiceErrors.NotFound"/> when there is none,
    /// <see cref="VersionMismatch"/> when the caller named a version that is no longer the current one,
    /// the invoice otherwise. No expected version means no precondition. A missing invoice wins over a
    /// stale version: without the condition the answer would have been a 404 (RFC 9110 §13.2.1).
    /// </summary>
    public static Result<Invoice> ToWrite(this Invoice? invoice, InvoiceId id, uint? expectedVersion) => invoice switch
    {
        null => Result.Failure<Invoice>(InvoiceErrors.NotFound(id)),
        _ when expectedVersion is { } expected && invoice.Version != expected => Result.Failure<Invoice>(VersionMismatch),
        _ => invoice,
    };
}
