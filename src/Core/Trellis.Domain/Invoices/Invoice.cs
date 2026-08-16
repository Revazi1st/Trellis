using Trellis.Domain.Common;
using Trellis.Domain.Invoices.Events;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Results;

namespace Trellis.Domain.Invoices;

/// <summary>
/// The reference aggregate for this codebase — copy its shape for new aggregates:
/// static factory + private EF constructor, invariants inside state transitions returning Result,
/// child entities behind a read-only view, domain events raised on transitions.
/// </summary>
public sealed class Invoice : AggregateRoot<InvoiceId>, IAuditable, ISoftDeletable
{
    private readonly List<InvoiceLine> _lines = [];

    private Invoice(InvoiceId id, string customerName, EmailAddress customerEmail, string currency)
        : base(id)
    {
        CustomerName = customerName;
        CustomerEmail = customerEmail;
        Currency = currency;
        Status = InvoiceStatus.Draft;
    }

    /// <summary>EF Core materialization only.</summary>
    private Invoice()
    {
    }

    public string CustomerName { get; private set; } = null!;

    public EmailAddress CustomerEmail { get; private set; } = null!;

    /// <summary>ISO-4217 currency every line must match.</summary>
    public string Currency { get; private set; } = null!;

    public InvoiceStatus Status { get; private set; }

    public DateTimeOffset? DueDate { get; private set; }

    public DateTimeOffset? PaidAtUtc { get; private set; }

    public IReadOnlyList<InvoiceLine> Lines => _lines.AsReadOnly();

    public Money Total => _lines.Aggregate(Money.Zero(Currency), static (sum, line) => sum + line.Amount);

    /// <summary>
    /// Optimistic-concurrency token (Postgres xmin, mapped in Persistence). Surfaces as the API's
    /// ETag so clients can do conditional requests; never set by domain code.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed",
        Justification = "EF Core writes the database-generated xmin value through this setter.")]
    public uint Version { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string? CreatedBy { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }

    public static Result<Invoice> Create(string? customerName, EmailAddress customerEmail, Common.Currency currency)
    {
        if (string.IsNullOrWhiteSpace(customerName))
        {
            return InvoiceErrors.EmptyCustomerName;
        }

        return new Invoice(InvoiceId.New(), customerName.Trim(), customerEmail, currency.Code);
    }

    public Result AddLine(string? description, Money amount)
    {
        if (Status != InvoiceStatus.Draft)
        {
            return Result.Failure(InvoiceErrors.NotDraft);
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return Result.Failure(InvoiceErrors.EmptyLineDescription);
        }

        if (amount.Currency != Currency)
        {
            return Result.Failure(InvoiceErrors.CurrencyMismatch);
        }

        _lines.Add(InvoiceLine.Create(description.Trim(), amount));
        return Result.Success();
    }

    public Result Issue(DateTimeOffset dueDate)
    {
        if (Status != InvoiceStatus.Draft)
        {
            return Result.Failure(InvoiceErrors.AlreadyFinalized);
        }

        if (_lines.Count == 0)
        {
            return Result.Failure(InvoiceErrors.NoLines);
        }

        Status = InvoiceStatus.Issued;
        DueDate = dueDate;
        Raise(new InvoiceIssuedDomainEvent(Id, Currency));
        return Result.Success();
    }

    public Result MarkPaid(DateTimeOffset paidAtUtc)
    {
        if (Status != InvoiceStatus.Issued)
        {
            return Result.Failure(InvoiceErrors.NotIssued);
        }

        Status = InvoiceStatus.Paid;
        PaidAtUtc = paidAtUtc;
        Raise(new InvoicePaidDomainEvent(Id, CustomerEmail.Value));
        return Result.Success();
    }

    public Result Void()
    {
        if (Status is InvoiceStatus.Paid or InvoiceStatus.Voided)
        {
            return Result.Failure(InvoiceErrors.AlreadyFinalized);
        }

        Status = InvoiceStatus.Voided;
        return Result.Success();
    }
}
