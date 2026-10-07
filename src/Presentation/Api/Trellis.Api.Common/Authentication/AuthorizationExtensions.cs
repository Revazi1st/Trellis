using Microsoft.AspNetCore.Builder;
using Trellis.Application.Authorization;

namespace Trellis.Api.Common.Authentication;

public static class AuthorizationExtensions
{
    /// <summary>
    /// Requires the caller to hold <paramref name="permission"/> (e.g. Permissions.Invoices.Write).
    /// Throws at startup, while the endpoint is mapped, when the name is not in
    /// <see cref="Permissions.All"/>: a misspelt permission is a programming error, and failing the
    /// boot is far easier to diagnose than an endpoint that answers 403 to everyone.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="permission"/> is not a catalogued permission.</exception>
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission)
    {
        if (!PermissionPolicyProvider.IsKnownPermission(permission))
        {
            throw new ArgumentException(
                $"'{permission}' is not a permission in {nameof(Permissions)}.{nameof(Permissions.All)}. "
                + "Add the constant to the catalogue (and grant it to a role) before requiring it.",
                nameof(permission));
        }

        return builder.RequireAuthorization(permission);
    }
}
