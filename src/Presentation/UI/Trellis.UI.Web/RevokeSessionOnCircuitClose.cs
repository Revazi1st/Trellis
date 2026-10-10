using Microsoft.AspNetCore.Components.Server.Circuits;
using Trellis.UI.Common.Auth;

namespace Trellis.UI.Web;

/// <summary>
/// Revokes the circuit's refresh token when the circuit closes (the tab was closed or reloaded and the
/// disconnected-circuit retention ran out). The tokens live only in the circuit (<see cref="CircuitTokenStore"/>),
/// so the session cannot be resumed anyway; revoking it at the API means a copy of the token cannot
/// resume it either. Best-effort, like sign-out (<see cref="TokenAuthenticationStateProvider.CloseSessionAsync"/>).
/// </summary>
internal sealed class RevokeSessionOnCircuitClose(TokenAuthenticationStateProvider authState) : CircuitHandler
{
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
        => authState.CloseSessionAsync(cancellationToken);
}
