using Trellis.Api.Contracts.Auth;

namespace Trellis.UI.Common.Services;

public interface IAuthApiClient
{
    Task<ApiResult<TokenResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    Task<ApiResult<TokenResponse>> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default);

    Task<ApiResult<Guid>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
}
