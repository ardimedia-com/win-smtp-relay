using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

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
        // IdentityDbContext reads the schema version from IdentityOptions via the application service
        // provider. Provide one set to Version3 so the design-time model (and thus migrations) include
        // the AspNetUserPasskeys table — matching the runtime configuration in AddRelayAdminAuth.
        var appServices = new ServiceCollection()
            .Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .BuildServiceProvider();

        var options = new DbContextOptionsBuilder<RelayDbContext>()
            .UseSqlite("Data Source=winsmtprelay-design.db",
                sqlite => sqlite.MigrationsAssembly("WinSmtpRelay.Storage"))
            .UseApplicationServiceProvider(appServices)
            .Options;

        // Design-time only: no tenant scope (filtering does not affect schema/migrations).
        return new RelayDbContext(options, new CurrentTenant());
    }
}
