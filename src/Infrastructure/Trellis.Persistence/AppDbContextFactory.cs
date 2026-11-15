using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Trellis.Persistence;

/// <summary>Lets 'dotnet ef' create the context at design time without a running host (scripts/ef.sh).</summary>
internal sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        PostgresDialect.UseDesignTime(options, Environment.GetEnvironmentVariable("TRELLIS_DESIGNTIME_DB"));

        return new AppDbContext(options.Options
#if (UseMultitenancy)
            , new DesignTimeTenantContext()
#endif
            );
    }

#if (UseMultitenancy)
    /// <summary>Design time has no request, so no tenant — the filter is inert while scaffolding migrations.</summary>
    private sealed class DesignTimeTenantContext : Trellis.Application.Abstractions.ITenantContext
    {
        public string? TenantId => null;
    }
#endif
}
