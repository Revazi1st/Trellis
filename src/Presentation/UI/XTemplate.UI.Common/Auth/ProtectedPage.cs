using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace XTemplate.UI.Common.Auth;

/// <summary>
/// Base for pages that require authentication. Guards at the COMPONENT level (not endpoint [Authorize]),
/// which is the correct model for client-token Blazor Server: endpoint authorization would try to
/// challenge a server auth scheme that doesn't exist here (ADR-0007). Anonymous users are redirected
/// to login; users lacking <see cref="RequiredPermission"/> are sent back to the invoice list.
/// </summary>
public abstract class ProtectedPage : ComponentBase
{
    [CascadingParameter]
    protected Task<AuthenticationState> AuthStateTask { get; set; } = default!;

    [Inject]
    protected NavigationManager Navigation { get; set; } = default!;

    protected ClaimsPrincipal User { get; private set; } = new(new ClaimsIdentity());

    /// <summary>A permission (role claim) the user must hold; null means "signed in is enough".</summary>
    protected virtual string? RequiredPermission => null;

    protected sealed override async Task OnInitializedAsync()
    {
        User = (await AuthStateTask).User;

        if (User.Identity?.IsAuthenticated != true)
        {
            Navigation.NavigateTo("login");
            return;
        }

        if (RequiredPermission is { } permission && !User.IsInRole(permission))
        {
            Navigation.NavigateTo("invoices");
            return;
        }

        await OnAuthorizedAsync();
    }

    /// <summary>Runs once the user is confirmed authenticated (and permitted). Load page data here.</summary>
    protected virtual Task OnAuthorizedAsync() => Task.CompletedTask;
}
