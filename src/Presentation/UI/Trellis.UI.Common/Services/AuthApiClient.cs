using System.Net.Http.Json;
using Trellis.Api.Contracts.Auth;

namespace Trellis.UI.Common.Services;

internal sealed class AuthApiClient(HttpClient httpClient) : IAuthApiClient
{
    public async Task<ApiResult<TokenResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await httpClient.PostAsJsonAsync("api/v1/auth/login", request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return ApiResult.Failure<TokenResponse>("Invalid email or password.");
        }

        TokenResponse? tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        return ApiResult.Success(tokens!);
    }

    public async Task<ApiResult<TokenResponse>> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await httpClient.PostAsJsonAsync("api/v1/auth/refresh", request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return ApiResult.Failure<TokenResponse>("Your session has expired. Please sign in again.");
        }

        TokenResponse? tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        return ApiResult.Success(tokens!);
    }

    public async Task<ApiResult<Guid>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await httpClient.PostAsJsonAsync("api/v1/auth/register", request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return ApiResult.Failure<Guid>("Registration failed. The email may already be in use.");
        }

        RegisterResponse? registered = await response.Content.ReadFromJsonAsync<RegisterResponse>(cancellationToken);
        return ApiResult.Success(registered!.UserId);
    }
}
