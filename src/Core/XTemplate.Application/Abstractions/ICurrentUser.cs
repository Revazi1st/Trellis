namespace XTemplate.Application.Abstractions;

/// <summary>The authenticated caller, if any. Implemented by Api.Common (HttpContext) and used by persistence auditing.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    string? UserName { get; }
}
