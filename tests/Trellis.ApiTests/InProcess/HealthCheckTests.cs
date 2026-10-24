using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;
using Trellis.Persistence;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// What readiness (<c>/health</c>) asks of the database: one check, the one named "database" that
/// <c>docs/deployment.md</c> documents. Aspire's <c>EnrichNpgsqlDbContext</c> registers a check of the same
/// context unless told not to, and every probe would then query PostgreSQL twice.
/// </summary>
public sealed class HealthCheckTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public void Readiness_checks_the_database_once_under_the_documented_name()
    {
        HealthCheckServiceOptions options = factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        using IServiceScope scope = factory.Services.CreateScope();

        string[] databaseChecks = [.. options.Registrations
            .Where(registration => IsAppDbContextCheck(registration.Factory(scope.ServiceProvider)))
            .Select(static registration => registration.Name)];

        databaseChecks.ShouldBe(["database"]);
    }

    /// <summary>EF Core's check type is internal; <c>AddDbContextCheck&lt;AppDbContext&gt;</c> is what creates one.</summary>
    private static bool IsAppDbContextCheck(IHealthCheck check)
    {
        Type type = check.GetType();
        return type.IsGenericType
               && string.Equals(type.GetGenericTypeDefinition().Name, "DbContextHealthCheck`1", StringComparison.Ordinal)
               && type.GetGenericArguments()[0] == typeof(AppDbContext);
    }
}
