using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Trellis.Api.Common.Http;
using Trellis.Application.Options;

namespace Trellis.Api.Common.Authentication;

public static class AuthenticationExtensions
{
    /// <summary>
    /// JWT bearer authentication + permission-based authorization (policies created on demand per
    /// permission). Signing configuration comes from the validated <see cref="AuthTokenOptions"/>
    /// registered by AddInfrastructure — binding the section a second time here would mean two
    /// sources of truth, only one of which is validated.
    /// </summary>
    public static IServiceCollection AddApiAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthTokenOptions>>((jwt, authTokenOptions) =>
            {
                AuthTokenOptions options = authTokenOptions.Value;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = options.Issuer,
                    ValidAudience = options.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
                    // Pin the one algorithm this host mints (JwtTokenProvider): a token whose header
                    // names any other algorithm is rejected before its signature is even considered,
                    // instead of being verified with whatever the attacker-controlled header asks for.
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                };

                // Auth rejections must speak the same RFC 9457 dialect as every other failure — the
                // default challenge is an empty body clients can't distinguish or display. Both go
                // through IProblemDetailsService (ADR-0024), so they carry code, type, correlationId and
                // traceId like a problem written from a Result.
                jwt.Events = new JwtBearerEvents
                {
#if (UseMultitenancy)
                    OnTokenValidated = RequireTenantClaimAsync,
#endif
                    OnChallenge = WriteChallengeAsync,
                    OnForbidden = static async context => await context.HttpContext.WriteProblemAsync(ProblemCatalog.Forbidden),
                };
            });

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddAuthorization();

        return services;
    }

#if (UseMultitenancy)
    /// <summary>
    /// Fail closed: a valid signature with no tenant (a token minted before the toggle was enabled,
    /// or an external IdP that does not emit it) must be a loud 401, not a silent read of
    /// system-scoped rows (ADR-0022). <c>Fail</c> turns the authenticate result into a failure, so
    /// the ordinary challenge below writes the 401 problem — no new response path.
    /// </summary>
    private static Task RequireTenantClaimAsync(TokenValidatedContext context)
    {
        if (string.IsNullOrWhiteSpace(
                context.Principal?.FindFirst(Trellis.Application.Abstractions.ITenantContext.ClaimType)?.Value))
        {
            context.Fail("The access token carries no tenant claim.");
        }

        return Task.CompletedTask;
    }
#endif

    private static async Task WriteChallengeAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();

        // RFC 6750 §3: a 401 from a bearer-protected resource names the scheme, and a
        // presented-but-rejected token says so, so a client knows to refresh rather than to prompt
        // for credentials. HandleResponse() suppressed the handler's default header.
        context.Response.Headers.WWWAuthenticate = context.AuthenticateFailure is null
            ? JwtBearerDefaults.AuthenticationScheme
            : $"{JwtBearerDefaults.AuthenticationScheme} error=\"invalid_token\"";

        await context.HttpContext.WriteProblemAsync(ProblemCatalog.Unauthenticated);
    }
}
