using Microsoft.AspNetCore.Authorization;

namespace XTemplate.Api.Common.Authentication;

/// <summary>Requires the principal to carry a specific permission claim.</summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}
