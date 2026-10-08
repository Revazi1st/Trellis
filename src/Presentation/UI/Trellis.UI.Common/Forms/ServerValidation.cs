using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Trellis.UI.Common.Services;

namespace Trellis.UI.Common.Forms;

/// <summary>
/// The API's validation errors on an <c>EditForm</c>: place <c>&lt;ServerValidation @ref="..." /&gt;</c>
/// next to <c>&lt;DataAnnotationsValidator /&gt;</c> and hand it each call's result
/// (<see cref="Show{T}"/>), which returns the sentence for the form's alert. The work is done by
/// <see cref="ServerValidationMessages"/>; this component only follows the form's <see cref="EditContext"/>
/// (a form whose model is replaced gets a new one).
/// </summary>
public sealed class ServerValidation : ComponentBase, IDisposable
{
    private EditContext? _editContext;
    private ServerValidationMessages? _messages;

    [CascadingParameter]
    private EditContext? CurrentEditContext { get; set; }

    /// <inheritdoc cref="ServerValidationMessages.Show{T}"/>
    public string? Show<T>(ApiResult<T> result)
        => (_messages ?? throw new InvalidOperationException($"{nameof(ServerValidation)} has not been rendered yet.")).Show(result);

    protected override void OnParametersSet()
    {
        if (CurrentEditContext is null)
        {
            throw new InvalidOperationException(
                $"{nameof(ServerValidation)} requires a cascading {nameof(EditContext)}: place it inside an EditForm.");
        }

        if (ReferenceEquals(CurrentEditContext, _editContext))
        {
            return;
        }

        _messages?.Dispose();
        _editContext = CurrentEditContext;
        _messages = new ServerValidationMessages(_editContext);
    }

    public void Dispose() => _messages?.Dispose();
}
