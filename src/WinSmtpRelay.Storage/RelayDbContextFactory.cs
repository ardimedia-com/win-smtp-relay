using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WinSmtpRelay.Storage;

/// <summary>
/// Design-time factory so <c>dotnet ef</c> can build the model and create migrations
/// without instantiating the full Windows-Service/WebApplication host.
/// Not used at runtime.
/// </summary>
public class RelayDbContextFactory : IDesignTimeDbContextFactory<RelayDbContext>
{
    public RelayDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RelayDbContext>()
            .UseSqlite("Data Source=winsmtprelay-design.db",
                sqlite => sqlite.MigrationsAssembly("WinSmtpRelay.Storage"))
            .Options;

        // Design-time only: no tenant scope (filtering does not affect schema/migrations).
        return new RelayDbContext(options, new CurrentTenant());
    }
}
