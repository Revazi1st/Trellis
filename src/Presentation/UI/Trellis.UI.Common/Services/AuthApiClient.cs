using System.Net.Http.Json;
using Trellis.Api.Contracts.Auth;

namespace Trellis.UI.Common.Services;

/// <summary>
/// Typed client for the anonymous auth endpoints. A failure carries the status and the API's problem
/// (<see cref="ApiResponses"/>): the sign-in page shows the lockout, rate-limit and password-policy
/// sentences as such, and the token provider tells a rejected refresh token (401) from a transient failure.
/// </summary>
internal sealed class AuthApiClient(HttpClient httpClient) : IAuthApiClient
{
    public async Task<ApiResult<TokenResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage? response = await ApiResponses.TrySendAsync(
            ct => httpClient.PostAsJsonAsync("api/v1/auth/login", request, ct), cancellationToken);

        if (response is not { IsSuccessStatusCode: true })
        {
            return await ApiResponses.FailureFromAsync<TokenResponse>(response, cancellationToken);
        }

        TokenResponse? tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        return ApiResult.Success(tokens!);
    }

    public async Task<ApiResult<TokenResponse>> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage? response = await ApiResponses.TrySendAsync(
            ct => httpClient.PostAsJsonAsync("api/v1/auth/refresh", request, ct), cancellationToken);

        if (response is not { IsSuccessStatusCode: true })
        {
            return await ApiResponses.FailureFromAsync<TokenResponse>(response, cancellationToken);
        }

        TokenResponse? tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        return ApiResult.Success(tokens!);
    }

    public async Task<ApiResult<Guid>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage? response = await ApiResponses.TrySendAsync(
            ct => httpClient.PostAsJsonAsync("api/v1/auth/register", request, ct), cancellationToken);

        if (response is not { IsSuccessStatusCode: true })
        {
            return await ApiResponses.FailureFromAsync<Guid>(response, cancellationToken);
        }

        RegisterResponse? registered = await response.Content.ReadFromJsonAsync<RegisterResponse>(cancellationToken);
        return ApiResult.Success(registered!.UserId);
    }
}
