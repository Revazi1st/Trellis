using System.Globalization;
using Trellis.SharedKernel.Results;

namespace Trellis.Domain.Invoices;

public static class InvoiceErrors
{
    public static readonly Error EmptyCustomerName =
        Error.Validation("Invoice.EmptyCustomerName", "A customer name is required.");

    public static readonly Error CustomerNameTooLong = Error.Validation(
        "Invoice.CustomerNameTooLong",
        string.Create(CultureInfo.InvariantCulture, $"A customer name has at most {Invoice.CustomerNameMaxLength} characters."));

    public static readonly Error NotDraft =
        Error.Conflict("Invoice.NotDraft", "Lines can only be changed while the invoice is a draft.");

    public static readonly Error NoLines =
        Error.Validation("Invoice.NoLines", "An invoice needs at least one line before it can be issued.");

    public static readonly Error DueDateInPast =
        Error.Validation("Invoice.DueDateInPast", "The due date is required and cannot be in the past.");

    public static readonly Error NotIssued =
        Error.Conflict("Invoice.NotIssued", "Only an issued invoice can be paid.");

    public static readonly Error AlreadyIssued =
        Error.Conflict("Invoice.AlreadyIssued", "The invoice has already been issued.");

    public static readonly Error AlreadyFinalized =
        Error.Conflict("Invoice.AlreadyFinalized", "A paid or voided invoice cannot be changed.");

    public static readonly Error EmptyLineDescription =
        Error.Validation("Invoice.EmptyLineDescription", "A line description is required.");

    public static readonly Error LineDescriptionTooLong = Error.Validation(
        "Invoice.LineDescriptionTooLong",
        string.Create(CultureInfo.InvariantCulture, $"A line description has at most {InvoiceLine.DescriptionMaxLength} characters."));

    public static readonly Error CurrencyMismatch =
        Error.Validation("Invoice.CurrencyMismatch", "The line currency must match the invoice currency.");

    public static Error NotFound(InvoiceId id) =>
        Error.NotFound("Invoice.NotFound", $"Invoice '{id}' was not found.");
}
