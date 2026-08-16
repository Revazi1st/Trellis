using Microsoft.AspNetCore.Authorization;
using Trellis.Application.Authorization;

namespace Trellis.Api.Common.Authentication;

/// <summary>Succeeds when the principal carries a "permission" claim matching the requirement.</summary>
internal sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        bool granted = context.User.Claims.Any(claim =>
            string.Equals(claim.Type, AuthClaims.Permission, StringComparison.Ordinal) &&
            string.Equals(claim.Value, requirement.Permission, StringComparison.Ordinal));

        if (granted)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
