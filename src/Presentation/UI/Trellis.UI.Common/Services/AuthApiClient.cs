using System.Net.Http.Json;
using Trellis.Api.Contracts.Auth;

namespace Trellis.UI.Common.Services;

/// <summary>
/// Typed client for the anonymous auth endpoints. A failure carries the status and the API's problem
/// (<see cref="ApiResponses"/>): the sign-in page shows the lockout, rate-limit and password-policy
/// sentences as such, and the token provider tells a rejected refresh token (401) from a transient failure.
/// Every call goes through <see cref="ApiCall"/>, so an unreachable API is a result, not an exception.
/// </summary>
internal sealed class AuthApiClient(HttpClient httpClient) : IAuthApiClient
{
    public Task<ApiResult<TokenResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
        => ApiCall.ReadAsync<TokenResponse>(
            ct => httpClient.PostAsJsonAsync("api/v1/auth/login", request, ct), cancellationToken);

    public Task<ApiResult<TokenResponse>> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default)
        => ApiCall.ReadAsync<TokenResponse>(
            ct => httpClient.PostAsJsonAsync("api/v1/auth/refresh", request, ct), cancellationToken);

    public Task<ApiResult<Guid>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
        => ApiCall.ReadAsync<RegisterResponse, Guid>(
            ct => httpClient.PostAsJsonAsync("api/v1/auth/register", request, ct),
            static registered => registered.UserId,
            cancellationToken);

    public Task<ApiResult<bool>> LogoutAsync(LogoutRequest request, CancellationToken cancellationToken = default)
        => ApiCall.SendAsync(ct => httpClient.PostAsJsonAsync("api/v1/auth/logout", request, ct), cancellationToken);
}
