using Microsoft.AspNetCore.Builder;

namespace XTemplate.Api.Common.Authentication;

public static class AuthorizationExtensions
{
    /// <summary>Requires the caller to hold <paramref name="permission"/> (e.g. Permissions.Invoices.Write).</summary>
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission)
        => builder.RequireAuthorization(permission);
}
