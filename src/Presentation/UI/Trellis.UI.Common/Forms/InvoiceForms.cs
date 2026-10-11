using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Trellis.Api.Contracts.Invoices;

namespace Trellis.UI.Common.Forms;

// The invoice forms' models. Their DataAnnotations mirror the API's validators (CreateInvoiceCommandValidator,
// AddInvoiceLineCommandValidator, IssueInvoiceCommandValidator and the domain rules a user can trip) so the
// common mistakes are caught before a round trip; the API stays the authority, and what it still rejects
// comes back through ServerValidation. Change a validator, change its mirror here. The lengths are the Domain's
// constants (Invoice.CustomerNameMaxLength, EmailAddress.MaxLength, Currency.CodeLength,
// InvoiceLine.DescriptionMaxLength), restated because UI.Common references only the contracts;
// InvoiceFormMirrorTests (ApiTests/Ui) fails when a constant and its restatement part.

/// <summary>The new-invoice form (<c>POST /invoices</c>).</summary>
public sealed class CreateInvoiceForm
{
    [Required]
    [MaxLength(200)]
    [Display(Name = "Customer name")]
    public string CustomerName { get; set; } = string.Empty;

    [Required]
    [MaxLength(254)]
    [EmailAddress]
    [Display(Name = "Customer email")]
    public string CustomerEmail { get; set; } = string.Empty;

    [Required]
    [StringLength(3, MinimumLength = 3, ErrorMessage = "The currency is a three-letter code, such as USD.")]
    [Display(Name = "Currency")]
    public string Currency { get; set; } = "USD";

    public CreateInvoiceRequest ToRequest() => new(CustomerName, CustomerEmail, Currency.ToUpperInvariant());
}

/// <summary>The add-line form (<c>POST /invoices/{id}/lines</c>).</summary>
public sealed class InvoiceLineForm
{
    [Required]
    [MaxLength(500)]
    [Display(Name = "Description")]
    public string Description { get; set; } = string.Empty;

    [Range(0d, double.MaxValue, ErrorMessage = "An amount cannot be negative.")]
    [Display(Name = "Amount")]
    public decimal Amount { get; set; }

    public AddInvoiceLineRequest ToRequest() => new(Description, Amount);
}

/// <summary>
/// The issue form (<c>POST /invoices/{id}/issue</c>). The picked day is sent as midnight UTC and the
/// API rejects a due date before its clock (<c>Invoice.DueDateInPast</c>), so today is already too
/// early: the earliest day it accepts is tomorrow (UTC), which is what <see cref="Earliest"/> holds and
/// the date picker's <c>min</c> shows.
/// </summary>
public sealed class IssueInvoiceForm : IValidatableObject
{
    private IssueInvoiceForm(DateTime earliest, DateTime dueDate)
    {
        Earliest = earliest;
        DueDate = dueDate;
    }

    /// <summary>The first day the API accepts as a due date.</summary>
    public DateTime Earliest { get; }

    /// <summary><see cref="Earliest"/> in the date input's format, for its <c>min</c> attribute.</summary>
    public string EarliestValue => Earliest.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Required(ErrorMessage = "A due date is required.")]
    [Display(Name = "Due date")]
    public DateTime? DueDate { get; set; }

    /// <summary>A form for <paramref name="now"/>: due in 14 days unless the user picks another day.</summary>
    public static IssueInvoiceForm At(DateTimeOffset now)
    {
        DateTime today = now.UtcDateTime.Date;
        return new IssueInvoiceForm(earliest: today.AddDays(1), dueDate: today.AddDays(14));
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DueDate is { } dueDate && dueDate.Date < Earliest)
        {
            yield return new ValidationResult(
                string.Create(CultureInfo.InvariantCulture, $"The due date cannot be in the past: pick {EarliestValue} or later."),
                [nameof(DueDate)]);
        }
    }

    /// <summary>The request for the picked day, as the UTC instant the API expects.</summary>
    public IssueInvoiceRequest ToRequest()
        => new(new DateTimeOffset(DateTime.SpecifyKind((DueDate ?? Earliest).Date, DateTimeKind.Utc)));
}
