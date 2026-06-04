using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

/// <summary>
/// Seeds configuration tables from appsettings.json on first startup (when tables are empty).
/// After seeding, SQLite becomes the source of truth for these settings.
/// </summary>
public class ConfigurationSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<SmtpListenerOptions> listenerOpts,
    IOptions<DeliveryOptions> deliveryOpts,
    IOptions<DkimOptions> dkimOpts,
    IOptions<RateLimitOptions> rateLimitOpts,
    IOptions<MessageFilterOptions> filterOpts,
    IOptions<AdminUiOptions> adminUiOpts,
    ILogger<ConfigurationSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();

        await SeedReceiveConnectorsAsync(db, cancellationToken);
        await SeedAcceptedDomainsAsync(db, cancellationToken);
        await SeedIpAccessRulesAsync(db, cancellationToken);
        await SeedSendConnectorsAsync(db, cancellationToken);
        await SeedDkimDomainsAsync(db, cancellationToken);
        await SeedRateLimitSettingsAsync(db, cancellationToken);
        await SeedPortalSettingsAsync(db, cancellationToken);
        await SeedMessageFiltersAsync(db, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedReceiveConnectorsAsync(RelayDbContext db, CancellationToken ct)
    {
        if (await db.ReceiveConnectors.AnyAsync(ct)) return;

        var opts = listenerOpts.Value;
        var order = 0;
        foreach (var ep in opts.Endpoints)
        {
            db.ReceiveConnectors.Add(new ReceiveConnector
            {
                Name = $"Endpoint {++order}",
                Address = ep.Address,
                Port = ep.Port,
                RequireTls = ep.RequireTls,
                ImplicitTls = ep.ImplicitTls,
                RequireAuth = ep.RequireAuth,
                MaxMessageSizeBytes = opts.MaxMessageSizeBytes,
                MaxConnections = opts.MaxConnections
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded {Count} receive connector(s) from appsettings", opts.Endpoints.Count);
    }

    private async Task SeedAcceptedDomainsAsync(RelayDbContext db, CancellationToken ct)
    {
        if (await db.AcceptedDomains.AnyAsync(ct)) return;

        var domains = listenerOpts.Value.AcceptedDomains;
        if (domains.Count == 0) return;

        foreach (var domain in domains)
        {
            db.AcceptedDomains.Add(new AcceptedDomain { Domain = domain.ToLowerInvariant().Trim() });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded {Count} accepted domain(s) from appsettings", domains.Count);
    }

    private async Task SeedIpAccessRulesAsync(RelayDbContext db, CancellationToken ct)
    {
        if (await db.IpAccessRules.AnyAsync(ct)) return;

        var networks = listenerOpts.Value.AllowedNetworks;
        if (networks.Count == 0) return;

        var order = 0;
        foreach (var network in networks)
        {
            db.IpAccessRules.Add(new IpAccessRule
            {
                Network = network,
                Action = IpAccessAction.Allow,
                SortOrder = ++order,
                Description = "Imported from appsettings"
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded {Count} IP access rule(s) from appsettings", networks.Count);
    }

    private async Task SeedSendConnectorsAsync(RelayDbContext db, CancellationToken ct)
    {
        if (await db.SendConnectors.AnyAsync(ct)) return;

        var opts = deliveryOpts.Value;

        // Create default send connector
        db.SendConnectors.Add(new SendConnector
        {
            Name = "Default",
            SmartHost = opts.SmartHost,
            SmartHostPort = opts.SmartHostPort,
            Username = opts.SmartHostUsername,
            EncryptedPassword = opts.SmartHostPassword, // plain for initial seed
            OpportunisticTls = opts.OpportunisticTls,
            IsDefault = true,
            MaxConcurrentDeliveries = opts.MaxConcurrentDeliveries,
            MaxRetryHours = opts.MaxRetryHours,
            RetryIntervalsMinutes = string.Join(",", opts.RetryIntervalsMinutes),
            ConnectTimeoutSeconds = opts.ConnectTimeoutSeconds
        });

        await db.SaveChangesAsync(ct);

        // Seed domain routes if any
        if (opts.DomainRoutes.Count > 0)
        {
            var defaultConnector = await db.SendConnectors.FirstAsync(c => c.IsDefault, ct);
            var order = 0;
            foreach (var route in opts.DomainRoutes)
            {
                db.DomainRoutes.Add(new DomainRoute
                {
                    DomainPattern = route.DomainPattern,
                    SendConnectorId = defaultConnector.Id,
                    SortOrder = ++order
                });
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded {Count} domain route(s) from appsettings", opts.DomainRoutes.Count);
        }

        logger.LogInformation("Seeded default send connector from appsettings");
    }

    private async Task SeedDkimDomainsAsync(RelayDbContext db, CancellationToken ct)
    {
        if (await db.DkimDomains.AnyAsync(ct)) return;

        var opts = dkimOpts.Value;
        if (opts.Domains.Count == 0) return;

        foreach (var domain in opts.Domains)
        {
            db.DkimDomains.Add(new DkimDomain
            {
                Domain = domain.Domain,
                Selector = domain.Selector,
                PrivateKeyPath = domain.PrivateKeyPath,
                IsEnabled = opts.Enabled
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded {Count} DKIM domain(s) from appsettings", opts.Domains.Count);
    }

    // Matches the HasData seeds in RelayDbContext: an untouched settings row carries this timestamp.
    private static readonly DateTime SeedSentinel = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private async Task SeedRateLimitSettingsAsync(RelayDbContext db, CancellationToken ct)
    {
        var existing = await db.RateLimitSettings.FindAsync([1], ct);
        if (existing is null) return; // seeded by EF HasData

        // Apply appsettings only while the row has never been edited (UI edits bump UpdatedUtc
        // past the seed sentinel). Once edited, the database is authoritative — do not clobber it.
        if (existing.UpdatedUtc != SeedSentinel)
            return;

        var opts = rateLimitOpts.Value;
        existing.MaxConnectionsPerIpPerMinute = opts.MaxConnectionsPerIpPerMinute;
        existing.MaxMessagesPerSenderPerMinute = opts.MaxMessagesPerSenderPerMinute;
        existing.MaxMessagesPerSenderPerDay = opts.MaxMessagesPerSenderPerDay;
        existing.FailedAuthBanThreshold = opts.FailedAuthBanThreshold;
        existing.FailedAuthBanMinutes = opts.FailedAuthBanMinutes;
        // Keep UpdatedUtc at the sentinel so appsettings remains the source until a UI edit.

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Applied rate limit settings from appsettings (row not yet edited)");
    }

    private async Task SeedPortalSettingsAsync(RelayDbContext db, CancellationToken ct)
    {
        var existing = await db.PortalSettings.FindAsync([1], ct);
        if (existing is null) return; // seeded by EF HasData

        // Apply appsettings only while the row has never been edited (the UI toggle bumps
        // UpdatedUtc past the seed sentinel). Once edited, the database is authoritative.
        if (existing.UpdatedUtc != SeedSentinel)
            return;

        existing.SelfServiceSignupEnabled = adminUiOpts.Value.SelfServiceSignupEnabled;
        // Keep UpdatedUtc at the sentinel so appsettings remains the source until a UI edit.
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Applied portal settings from appsettings (row not yet edited)");
    }

    private async Task SeedMessageFiltersAsync(RelayDbContext db, CancellationToken ct)
    {
        var opts = filterOpts.Value;

        if (!await db.HeaderRewriteEntries.AnyAsync(ct) && opts.HeaderRewrites.Count > 0)
        {
            var order = 0;
            foreach (var rule in opts.HeaderRewrites)
            {
                db.HeaderRewriteEntries.Add(new HeaderRewriteEntry
                {
                    HeaderName = rule.HeaderName,
                    MatchValue = rule.MatchValue,
                    Action = rule.Action,
                    NewValue = rule.NewValue,
                    SortOrder = ++order
                });
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded {Count} header rewrite rule(s) from appsettings", opts.HeaderRewrites.Count);
        }

        if (!await db.SenderRewriteEntries.AnyAsync(ct) && opts.SenderRewrites.Count > 0)
        {
            var order = 0;
            foreach (var rule in opts.SenderRewrites)
            {
                db.SenderRewriteEntries.Add(new SenderRewriteEntry
                {
                    FromPattern = rule.FromPattern,
                    ToAddress = rule.ToAddress,
                    SortOrder = ++order
                });
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded {Count} sender rewrite rule(s) from appsettings", opts.SenderRewrites.Count);
        }
    }
}
