using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Trellis.Application.Options;

namespace Trellis.Api.Common.Authentication;

public static class AuthenticationExtensions
{
    /// <summary>JWT bearer authentication + permission-based authorization (policies created on demand per permission).</summary>
    public static IServiceCollection AddApiAuthentication(this IServiceCollection services, AuthTokenOptions options)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = options.Issuer,
                    ValidAudience = options.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
                    ClockSkew = TimeSpan.FromSeconds(30),
                };

                // Auth rejections must speak the same RFC 9457 dialect as every other failure —
                // the default challenge is an empty body clients can't distinguish or display.
                jwt.Events = new JwtBearerEvents
                {
                    OnChallenge = static async context =>
                    {
                        context.HandleResponse();
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await context.Response.WriteAsJsonAsync(
                            new Microsoft.AspNetCore.Mvc.ProblemDetails
                            {
                                Title = "Unauthorized",
                                Detail = "A valid access token is required. Obtain one from POST /auth/login.",
                                Status = StatusCodes.Status401Unauthorized,
                            },
                            options: null,
                            contentType: "application/problem+json",
                            context.HttpContext.RequestAborted);
                    },
                    OnForbidden = static async context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(
                            new Microsoft.AspNetCore.Mvc.ProblemDetails
                            {
                                Title = "Forbidden",
                                Detail = "The authenticated user lacks the permission this endpoint requires.",
                                Status = StatusCodes.Status403Forbidden,
                            },
                            options: null,
                            contentType: "application/problem+json",
                            context.HttpContext.RequestAborted);
                    },
                };
            });

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddAuthorization();

        return services;
    }
}
