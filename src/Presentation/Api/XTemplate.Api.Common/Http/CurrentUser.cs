using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using XTemplate.Application.Abstractions;

namespace XTemplate.Api.Common.Http;

/// <summary>ICurrentUser adapter over the ambient HttpContext; anonymous requests yield nulls.</summary>
internal sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public Guid? UserId
        => Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out Guid id) ? id : null;

    public string? UserName => Principal?.FindFirstValue(ClaimTypes.Name);
}
