#if (IncludeUI)
using Microsoft.AspNetCore.Components.Forms;
using Shouldly;
using Trellis.Api.Contracts.Common;
using Trellis.UI.Common.Forms;
using Trellis.UI.Common.Services;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// The API's <c>errors[]</c> on a form (<see cref="ServerValidationMessages"/>): each field error under the
/// input it names, grouped by field; whole-request errors (null field) and fields the form does not have in
/// the alert; one announcement per failure; server messages gone once their input is edited or the form is
/// submitted again.
/// </summary>
public sealed class ServerValidationTests
{
    private const string InvalidInput = "Some of the entered values are invalid.";

    [Fact]
    public void Field_errors_are_grouped_under_the_property_they_name_case_insensitively()
    {
        var model = new CreateInvoiceForm();
        var editContext = new EditContext(model);
        var store = new ValidationMessageStore(editContext);

        IReadOnlyList<FieldError> unplaced = ServerValidationMessages.Place(editContext, store,
        [
            new FieldError("customerName", "Validation.NotEmpty", "'Customer Name' must not be empty."),
            new FieldError("currency", "Validation.ExactLength", "'Currency' must be 3 characters in length."),
            new FieldError("CUSTOMERNAME", "Validation.MaximumLength", "'Customer Name' is too long."),
        ]);

        unplaced.ShouldBeEmpty();
        editContext.GetValidationMessages(() => model.CustomerName)
            .ShouldBe(["'Customer Name' must not be empty.", "'Customer Name' is too long."]);
        editContext.GetValidationMessages(() => model.Currency).ShouldBe(["'Currency' must be 3 characters in length."]);
        editContext.GetValidationMessages(() => model.CustomerEmail).ShouldBeEmpty();
    }

    [Fact]
    public void Whole_request_nested_and_unknown_fields_are_returned_in_order_and_not_placed()
    {
        var model = new InvoiceLineForm();
        var editContext = new EditContext(model);
        var store = new ValidationMessageStore(editContext);
        FieldError wholeRequest = new(null, "Money.TooManyDecimalPlaces", "Amounts carry at most two decimal places.");
        FieldError nested = new("lines[0].amount", "Validation.GreaterThanOrEqual", "'Amount' must be at least 0.");
        FieldError unknown = new("invoiceId", "Validation.NotEmpty", "'Invoice Id' must not be empty.");

        IReadOnlyList<FieldError> unplaced = ServerValidationMessages.Place(
            editContext, store, [wholeRequest, new FieldError("description", "Validation.NotEmpty", "Say what it is."), nested, unknown]);

        unplaced.ShouldBe([wholeRequest, nested, unknown]);
        editContext.GetValidationMessages().ShouldBe(["Say what it is."]);
    }

    [Fact]
    public void Placing_again_replaces_the_previous_server_messages()
    {
        var model = new CreateInvoiceForm();
        var editContext = new EditContext(model);
        var store = new ValidationMessageStore(editContext);
        ServerValidationMessages.Place(editContext, store, [new FieldError("customerName", "Validation.NotEmpty", "first")]);

        ServerValidationMessages.Place(editContext, store, [new FieldError("currency", "Validation.ExactLength", "second")]);

        editContext.GetValidationMessages().ShouldBe(["second"]);
    }

    [Fact]
    public void Every_error_placed_announces_one_short_sentence()
    {
        var model = new CreateInvoiceForm();
        var editContext = new EditContext(model);
        using var server = new ServerValidationMessages(editContext);

        string? alert = server.Show(ValidationFailure(
            new FieldError("customerName", "Validation.NotEmpty", "'Customer Name' must not be empty."),
            new FieldError("customerEmail", "Validation.NotEmpty", "'Customer Email' must not be empty.")));

        alert.ShouldBe(InvalidInput);
        editContext.GetValidationMessages().Count().ShouldBe(2);
    }

    [Fact]
    public void Whole_request_errors_beside_placed_ones_are_the_alert()
    {
        var model = new InvoiceLineForm();
        var editContext = new EditContext(model);
        using var server = new ServerValidationMessages(editContext);

        string? alert = server.Show(ValidationFailure(
            new FieldError("amount", "Money.TooManyDecimalPlaces", "Amounts carry at most two decimal places."),
            new FieldError(null, "Invoice.CurrencyMismatch", "The line currency must match the invoice currency.")));

        alert.ShouldBe("The line currency must match the invoice currency.");
        editContext.GetValidationMessages(() => model.Amount).ShouldHaveSingleItem();
    }

    [Fact]
    public void A_value_object_error_the_api_attributed_lands_under_its_input()
    {
        // POST /invoices with "a@b": the UI's [EmailAddress] lets it through, the API's EmailAddress value
        // object rejects it and the handler names the field (ADR-0024 amendment), so it sits under the input.
        var model = new CreateInvoiceForm { CustomerName = "ACME", CustomerEmail = "a@b" };
        var editContext = new EditContext(model);
        using var server = new ServerValidationMessages(editContext);

        string? alert = server.Show(ApiResult.Failure<bool>(
            "The email address is not in a valid format.",
            400,
            Problem("EmailAddress.Invalid", new FieldError("customerEmail", "EmailAddress.Invalid", "The email address is not in a valid format."))));

        alert.ShouldBe(InvalidInput);
        editContext.GetValidationMessages(() => model.CustomerEmail).ShouldBe(["The email address is not in a valid format."]);
        editContext.GetValidationMessages(() => model.CustomerName).ShouldBeEmpty();
    }

    [Fact]
    public void A_failure_with_nothing_to_place_shows_the_described_sentence_inline()
    {
        var model = IssueInvoiceForm.At(UiHarness.Start);
        var editContext = new EditContext(model);
        using var server = new ServerValidationMessages(editContext);

        string? noLines = server.Show(ApiResult.Failure<bool>(
            "An invoice needs at least one line before it can be issued.",
            400,
            Problem("Invoice.NoLines", new FieldError(null, "Invoice.NoLines", "An invoice needs at least one line before it can be issued."))));
        string? conflict = server.Show(ApiResult.Failure<bool>(
            "Lines can only be changed while the invoice is a draft.", 409, Problem("Invoice.NotDraft")));

        noLines.ShouldBe("An invoice needs at least one line before it can be issued.");
        conflict.ShouldBe("Lines can only be changed while the invoice is a draft.");
        editContext.GetValidationMessages().ShouldBeEmpty();
    }

    [Fact]
    public void Editing_a_field_clears_its_server_message_and_resubmitting_clears_them_all()
    {
        var model = new CreateInvoiceForm();
        var editContext = new EditContext(model);
        using var server = new ServerValidationMessages(editContext);
        server.Show(ValidationFailure(
            new FieldError("customerName", "Validation.NotEmpty", "name"),
            new FieldError("currency", "Validation.ExactLength", "currency")));

        editContext.NotifyFieldChanged(editContext.Field(nameof(CreateInvoiceForm.CustomerName)));
        editContext.GetValidationMessages().ShouldBe(["currency"]);

        // EditForm validates on submit: no stale server message may block the next attempt.
        editContext.Validate().ShouldBeTrue();
        editContext.GetValidationMessages().ShouldBeEmpty();
    }

    [Fact]
    public void A_success_clears_the_server_messages()
    {
        var model = new CreateInvoiceForm();
        var editContext = new EditContext(model);
        using var server = new ServerValidationMessages(editContext);
        server.Show(ValidationFailure(new FieldError("customerName", "Validation.NotEmpty", "name")));

        server.Show(ApiResult.Success(true)).ShouldBeNull();

        editContext.GetValidationMessages().ShouldBeEmpty();
    }

    private static ApiResult<bool> ValidationFailure(params FieldError[] errors)
        => ApiResult.Failure<bool>(
            string.Join(' ', errors.Select(static e => e.Description)), 400, Problem("Validation.General", errors));

    private static ProblemResponse Problem(string code, params FieldError[] errors)
        => new(
            Type: "urn:problem-type:" + code,
            Title: null,
            Status: null,
            Detail: null,
            Instance: null,
            Code: code,
            CorrelationId: null,
            TraceId: null,
            Errors: errors.Length == 0 ? null : errors);
}
#endif
