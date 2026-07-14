using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace XTemplate.Persistence;

/// <summary>Lets 'dotnet ef' create the context at design time without a running host (scripts/ef.sh).</summary>
internal sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    [SuppressMessage("Security", "S2068:Hard-coded credentials are security-sensitive",
        Justification = "Design-time-only throwaway localhost credentials; migrations never run against real databases from here.")]
    public AppDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=xtemplate;Username=postgres;Password=postgres")
            .Options);
}
