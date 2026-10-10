using System.Collections.Frozen;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Trellis.Application.Authorization;

namespace Trellis.Api.Common.Authentication;

/// <summary>
/// Materializes the authorization policy for a permission in the catalogue (the policy name IS the
/// permission), so permissions need no up-front <c>AddPolicy</c> registration. Only names in
/// <see cref="Permissions.All"/> are synthesized; any other name falls back to the default provider,
/// so ordinary named policies keep working and a typo is reported by the framework as an unknown
/// policy instead of becoming a permission nobody holds (a silent, undiagnosable 403).
/// <see cref="AuthorizationExtensions.RequirePermission"/> rejects unknown names at startup.
/// </summary>
internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : IAuthorizationPolicyProvider
{
    // Built once: the catalogue is static, and the policies are immutable.
    private static readonly FrozenDictionary<string, AuthorizationPolicy> PermissionPolicies =
        Permissions.All.ToFrozenDictionary(
            static permission => permission,
            static permission => new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission))
                .Build(),
            StringComparer.Ordinal);

    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    /// <summary>The policies are a pure function of their name, so the per-endpoint policy cache is safe.</summary>
    public bool AllowsCachingPolicies => true;

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
        => PermissionPolicies.TryGetValue(policyName, out AuthorizationPolicy? policy)
            ? Task.FromResult<AuthorizationPolicy?>(policy)
            : _fallback.GetPolicyAsync(policyName);

    /// <summary>True when <paramref name="permission"/> is in the permission catalogue.</summary>
    internal static bool IsKnownPermission(string permission) => PermissionPolicies.ContainsKey(permission);
}
