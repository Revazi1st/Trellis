using XTemplate.Api.Contracts.Auth;

namespace XTemplate.UI.Common.Services;

public interface IAuthApiClient
{
    Task<ApiResult<TokenResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    Task<ApiResult<Guid>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
}
