using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Trellis.UI.Common.Auth;

/// <summary>
/// Base for pages that require authentication. Guards at the COMPONENT level (not endpoint [Authorize]),
/// which is the correct model for client-token Blazor Server: endpoint authorization would try to
/// challenge a server auth scheme that doesn't exist here (ADR-0007). Anonymous users are redirected
/// to login, carrying this page as the <c>returnUrl</c> (<see cref="LoginRedirect"/>); users lacking
/// <see cref="RequiredPermission"/> are sent back to the invoice list.
/// <para>
/// The guard runs again whenever the auth state changes while the page is open — a sign-out, or a
/// session ended by a rejected refresh token — so the page never lingers for a user who is gone.
/// </para>
/// </summary>
public abstract class ProtectedPage : ComponentBase
{
    /// <summary>The state the guard last ran against; the cascade hands down a new task on every change.</summary>
    private Task<AuthenticationState>? _guardedState;

    [CascadingParameter]
    protected Task<AuthenticationState> AuthStateTask { get; set; } = default!;

    [Inject]
    protected NavigationManager Navigation { get; set; } = default!;

    protected ClaimsPrincipal User { get; private set; } = new(new ClaimsIdentity());

    /// <summary>A permission (role claim) the user must hold; null means "signed in is enough".</summary>
    protected virtual string? RequiredPermission => null;

    protected sealed override async Task OnInitializedAsync()
    {
        _guardedState = AuthStateTask;
        if (await GuardAsync())
        {
            await OnAuthorizedAsync();
        }
    }

    /// <summary>
    /// Re-runs the guard when <c>CascadingAuthenticationState</c> hands down a new state (it does on every
    /// <c>AuthenticationStateChanged</c>). A page that overrides this must call the base.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        if (!ReferenceEquals(_guardedState, AuthStateTask))
        {
            _guardedState = AuthStateTask;
            await GuardAsync();
        }
    }

    /// <summary>Runs once the user is confirmed authenticated (and permitted). Load page data here.</summary>
    protected virtual Task OnAuthorizedAsync() => Task.CompletedTask;

    /// <summary>Sends the user elsewhere when they may not see this page; true when they may.</summary>
    private async Task<bool> GuardAsync()
    {
        User = (await AuthStateTask).User;

        if (User.Identity?.IsAuthenticated != true)
        {
            Navigation.NavigateTo(LoginRedirect.From(Navigation.ToBaseRelativePath(Navigation.Uri)));
            return false;
        }

        if (RequiredPermission is { } permission && !User.IsInRole(permission))
        {
            Navigation.NavigateTo("invoices");
            return false;
        }

        return true;
    }
}
