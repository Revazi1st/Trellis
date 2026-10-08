using System.Reflection;
using Microsoft.AspNetCore.Components.Forms;
using Trellis.Api.Contracts.Common;
using Trellis.UI.Common.Services;

namespace Trellis.UI.Common.Forms;

/// <summary>
/// Puts what the API rejected next to the inputs it is about. A failed call's field errors
/// (<see cref="ApiResult{T}.Errors"/>) go into a <see cref="ValidationMessageStore"/> on the form's
/// <see cref="EditContext"/>, grouped by field and matched to the model property of the same name
/// (case-insensitively: the wire says <c>customerName</c>, the model <c>CustomerName</c>), so each
/// shows in its input's <c>ValidationMessage</c> and the input is marked <c>aria-invalid</c>. An error
/// about the request as a whole (<see cref="FieldError.Field"/> null, e.g. <c>Invoice.DueDateInPast</c>)
/// or about a member the form does not have stays out of the store: it is part of the sentence
/// <see cref="Show{T}"/> returns for the form's alert.
/// <para>
/// A server message leaves when its input is edited, and all of them when the form is submitted
/// again (so a stale server message never blocks the next submit).
/// </para>
/// </summary>
public sealed class ServerValidationMessages : IDisposable
{
    private readonly EditContext _editContext;
    private readonly ValidationMessageStore _messages;

    public ServerValidationMessages(EditContext editContext)
    {
        ArgumentNullException.ThrowIfNull(editContext);
        _editContext = editContext;
        _messages = new ValidationMessageStore(editContext);
        _editContext.OnValidationRequested += ClearAll;
        _editContext.OnFieldChanged += ClearField;
    }

    /// <summary>
    /// Shows a call's outcome on the form and returns the one sentence for the form's alert, or null
    /// when there is nothing to announce (a success clears earlier server messages). When some errors
    /// sit next to their inputs, the alert carries only the rest (whole-request errors), or a short
    /// "some values are invalid" when every error found its input: each failure is read out once.
    /// </summary>
    public string? Show<T>(ApiResult<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsSuccess)
        {
            Clear();
            return null;
        }

        IReadOnlyList<FieldError> unplaced = Place(_editContext, _messages, result.Errors);
        _editContext.NotifyValidationStateChanged();

        bool anyPlaced = unplaced.Count < result.Errors.Count;
        if (!anyPlaced)
        {
            return result.Error;
        }

        return unplaced.Count > 0
            ? string.Join(' ', unplaced.Select(static error => error.Description))
            : ApiResponses.InvalidInput;
    }

    /// <summary>Removes every message this store added.</summary>
    public void Clear()
    {
        _messages.Clear();
        _editContext.NotifyValidationStateChanged();
    }

    /// <summary>
    /// Replaces <paramref name="messages"/>' content with <paramref name="errors"/>, grouped by the model
    /// property each names, and returns the errors that name none (null field, a nested path such as
    /// <c>lines[0].amount</c>, or a member the model does not have), in their original order.
    /// </summary>
    public static IReadOnlyList<FieldError> Place(
        EditContext editContext, ValidationMessageStore messages, IEnumerable<FieldError> errors)
    {
        ArgumentNullException.ThrowIfNull(editContext);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(errors);

        messages.Clear();
        List<FieldError> unplaced = [];
        IEnumerable<IGrouping<string?, FieldError>> byProperty =
            errors.GroupBy(error => PropertyNamed(editContext.Model, error.Field), StringComparer.Ordinal);
        foreach (IGrouping<string?, FieldError> field in byProperty)
        {
            if (field.Key is null)
            {
                unplaced.AddRange(field);
            }
            else
            {
                messages.Add(editContext.Field(field.Key), field.Select(static error => error.Description));
            }
        }

        return unplaced;
    }

    public void Dispose()
    {
        _editContext.OnValidationRequested -= ClearAll;
        _editContext.OnFieldChanged -= ClearField;
    }

    /// <summary>The model's public property the wire's field names; null for a whole-request error or a path the flat form models do not have.</summary>
    private static string? PropertyNamed(object model, string? field)
    {
        if (string.IsNullOrEmpty(field) || field.AsSpan().IndexOfAny('.', '[') >= 0)
        {
            return null;
        }

        return model.GetType().GetProperty(field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.Name;
    }

    private void ClearAll(object? sender, ValidationRequestedEventArgs e) => _messages.Clear();

    private void ClearField(object? sender, FieldChangedEventArgs e)
    {
        _messages.Clear(e.FieldIdentifier);
        _editContext.NotifyValidationStateChanged();
    }
}
