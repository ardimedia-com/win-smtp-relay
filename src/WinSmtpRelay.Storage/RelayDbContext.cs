using System.Reflection;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.Storage;

public class RelayDbContext(DbContextOptions<RelayDbContext> options, ICurrentTenant currentTenant)
    : IdentityDbContext<AdminUser, AdminRole, int>(options)
{
    private readonly ICurrentTenant _currentTenant = currentTenant;

    public DbSet<QueuedMessage> QueuedMessages => Set<QueuedMessage>();
    public DbSet<DeliveryLog> DeliveryLogs => Set<DeliveryLog>();
    public DbSet<RelayUser> RelayUsers => Set<RelayUser>();
    public DbSet<DailyStatistics> DailyStatistics => Set<DailyStatistics>();

    // Multi-tenancy + admin auth
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    // Configuration entities (runtime-editable via Admin UI)
    public DbSet<ReceiveConnector> ReceiveConnectors => Set<ReceiveConnector>();
    public DbSet<AcceptedDomain> AcceptedDomains => Set<AcceptedDomain>();
    public DbSet<AcceptedSenderDomain> AcceptedSenderDomains => Set<AcceptedSenderDomain>();
    public DbSet<IpAccessRule> IpAccessRules => Set<IpAccessRule>();
    public DbSet<SendConnector> SendConnectors => Set<SendConnector>();
    public DbSet<DomainRoute> DomainRoutes => Set<DomainRoute>();
    public DbSet<DkimDomain> DkimDomains => Set<DkimDomain>();
    public DbSet<RateLimitSettings> RateLimitSettings => Set<RateLimitSettings>();
    public DbSet<PortalSettings> PortalSettings => Set<PortalSettings>();
    public DbSet<EmailAuthSettings> EmailAuthSettings => Set<EmailAuthSettings>();
    public DbSet<BackupMxSettings> BackupMxSettings => Set<BackupMxSettings>();
    public DbSet<StatisticsRetentionSettings> StatisticsRetentionSettings => Set<StatisticsRetentionSettings>();
    public DbSet<DnsSettings> DnsSettings => Set<DnsSettings>();
    public DbSet<HeaderRewriteEntry> HeaderRewriteEntries => Set<HeaderRewriteEntry>();
    public DbSet<SenderRewriteEntry> SenderRewriteEntries => Set<SenderRewriteEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // SQLite has no native DateTimeOffset: the default mapping can't ORDER BY it. Store every
        // DateTimeOffset as a fixed-width, UTC-normalised ISO-8601 string ("...Z"). That keeps the value
        // human-readable and offset-marked in the database, and — because the format is fixed width —
        // lexicographic order equals chronological order, so SQLite can ORDER BY and range-filter (>=, <)
        // on it. The CLR/API type stays DateTimeOffset; on a provider with native support (SQL Server,
        // PostgreSQL) this convention can be dropped and the offset persisted directly.
        // (Note: a comparison on a *nullable* DateTimeOffset column combined with an OR-null still can't
        //  be translated — see MessageQueue.GetPendingAsync, which filters eligibility in memory.)
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcIsoDateTimeOffsetConverter>();
    }

    /// <summary>Round-trips a DateTimeOffset as a fixed-width UTC ISO-8601 string (sortable in SQLite).</summary>
    private sealed class UtcIsoDateTimeOffsetConverter()
        : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, string>(
            v => v.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            v => DateTimeOffset.Parse(v, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // Identity tables (AspNetUsers, AspNetRoles, ...)

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Slug).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.Slug).HasMaxLength(100);
            entity.Property(e => e.EgressIpAddress).HasMaxLength(45);
            entity.HasData(new Tenant
            {
                Id = TenantDefaults.DefaultTenantId,
                Name = TenantDefaults.DefaultName,
                Slug = TenantDefaults.DefaultSlug,
                IsEnabled = true,
                CreatedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            });
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.KeyPrefix);
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.KeyPrefix).HasMaxLength(16);
            entity.Property(e => e.KeyHash).HasMaxLength(64);
            entity.Property(e => e.Role).HasMaxLength(50);
        });

        modelBuilder.Entity<Identity.AdminUser>(entity =>
        {
            entity.Property(e => e.DisplayName).HasMaxLength(200);
        });

        modelBuilder.Entity<QueuedMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.NextRetryUtc);
            entity.HasIndex(e => e.MessageId);
            entity.Property(e => e.Sender).HasMaxLength(320);
            entity.Property(e => e.MessageId).HasMaxLength(255);
            entity.Property(e => e.SourceIp).HasMaxLength(45);
            entity.Property(e => e.AuthenticatedUser).HasMaxLength(255);
        });

        modelBuilder.Entity<DeliveryLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.QueuedMessageId);
            entity.HasIndex(e => e.TimestampUtc);
            entity.Property(e => e.Recipient).HasMaxLength(320);
            entity.Property(e => e.StatusCode).HasMaxLength(10);
            entity.Property(e => e.RemoteServer).HasMaxLength(255);
        });

        modelBuilder.Entity<RelayUser>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TenantId, e.Username }).IsUnique();
            entity.Property(e => e.Username).HasMaxLength(255);
        });

        modelBuilder.Entity<DailyStatistics>(entity =>
        {
            entity.HasKey(e => new { e.TenantId, e.Date });
        });

        // Configuration entities

        modelBuilder.Entity<ReceiveConnector>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.Address).HasMaxLength(45);
        });

        modelBuilder.Entity<AcceptedDomain>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Globally unique: a recipient domain may be claimed by only one tenant.
            entity.HasIndex(e => e.Domain).IsUnique();
            entity.Property(e => e.Domain).HasMaxLength(255);
            entity.Property(e => e.VerificationToken).HasMaxLength(64);
        });

        modelBuilder.Entity<AcceptedSenderDomain>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Globally unique: a sender domain may be claimed by only one tenant.
            entity.HasIndex(e => e.Domain).IsUnique();
            entity.Property(e => e.Domain).HasMaxLength(255);
            entity.Property(e => e.VerificationToken).HasMaxLength(64);
        });

        modelBuilder.Entity<IpAccessRule>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SortOrder);
            entity.Property(e => e.Network).HasMaxLength(50);
            entity.Property(e => e.Description).HasMaxLength(255);
        });

        modelBuilder.Entity<SendConnector>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.SmartHost).HasMaxLength(255);
            entity.Property(e => e.Username).HasMaxLength(255);
            entity.Property(e => e.RetryIntervalsMinutes).HasMaxLength(100);
        });

        modelBuilder.Entity<DomainRoute>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SortOrder);
            entity.Property(e => e.DomainPattern).HasMaxLength(255);
            entity.HasOne(e => e.SendConnector)
                .WithMany(s => s.DomainRoutes)
                .HasForeignKey(e => e.SendConnectorId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DkimDomain>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TenantId, e.Domain }).IsUnique();
            entity.Property(e => e.Domain).HasMaxLength(255);
            entity.Property(e => e.Selector).HasMaxLength(100);
            entity.Property(e => e.PrivateKeyPath).HasMaxLength(500);
        });

        modelBuilder.Entity<RateLimitSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasData(new RateLimitSettings
            {
                Id = 1,
                UpdatedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            });
        });

        modelBuilder.Entity<PortalSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SignupFromAddress).HasMaxLength(320);
            entity.HasData(new PortalSettings
            {
                Id = 1,
                SelfServiceSignupEnabled = false,
                SignupMaxAttemptsPerIpPerHour = 5,
                UpdatedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            });
        });

        modelBuilder.Entity<EmailAuthSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasData(new EmailAuthSettings
            {
                Id = 1,
                SpfEnabled = false,
                DmarcEnabled = false,
                Enforcement = WinSmtpRelay.Core.Configuration.EnforcementMode.LogOnly,
                RequireSenderDomainVerification = false,
                RequireRecipientDomainVerification = false,
                BindTenantToAllowIpRule = false,
                RejectUnresolvedTenant = false,
                UpdatedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            });
        });

        modelBuilder.Entity<BackupMxSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Domains).HasMaxLength(2000);
            entity.HasData(new BackupMxSettings
            {
                Id = 1,
                Enabled = false,
                Domains = "",
                RetryIntervalMinutes = 15,
                MaxHoldHours = 168,
                UpdatedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            });
        });

        modelBuilder.Entity<StatisticsRetentionSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AggregationTimeUtc).HasMaxLength(5);
            entity.HasData(new StatisticsRetentionSettings
            {
                Id = 1,
                RetentionDays = 90,
                AggregationTimeUtc = "00:00",
                UpdatedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            });
        });

        modelBuilder.Entity<DnsSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PublicHostname).HasMaxLength(255);
            entity.Property(e => e.SendingIpAddresses).HasMaxLength(1000);
            entity.Property(e => e.SpfIncludes).HasMaxLength(2000);
            entity.Property(e => e.SpfAllQualifier).HasMaxLength(20);
            entity.Property(e => e.DmarcReportEmail).HasMaxLength(320);
            entity.Property(e => e.DmarcPolicy).HasMaxLength(20);
            entity.HasData(new DnsSettings
            {
                Id = 1,
                SendingIpAddresses = "",
                SpfIncludes = "",
                SpfAllQualifier = "~all",
                DmarcPolicy = "none",
                DmarcPercentage = 100,
                UpdatedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            });
        });

        modelBuilder.Entity<HeaderRewriteEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SortOrder);
            entity.Property(e => e.HeaderName).HasMaxLength(255);
            entity.Property(e => e.Action).HasMaxLength(20);
        });

        modelBuilder.Entity<SenderRewriteEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SortOrder);
            entity.Property(e => e.FromPattern).HasMaxLength(320);
            entity.Property(e => e.ToAddress).HasMaxLength(320);
        });

        // Tenant partitioning: every ITenantOwned entity defaults to the Default tenant and
        // gets a restricted FK to Tenant (which also indexes TenantId). Tenant-scoped query
        // filters are applied in a later phase.
        var tenantOwned = modelBuilder.Model.GetEntityTypes()
            .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType))
            .ToList();
        var applyFilter = typeof(RelayDbContext)
            .GetMethod(nameof(ApplyTenantQueryFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var entityType in tenantOwned)
        {
            var builder = modelBuilder.Entity(entityType.ClrType);
            builder.Property(nameof(ITenantOwned.TenantId)).HasDefaultValue(TenantDefaults.DefaultTenantId);
            builder.HasOne(typeof(Tenant))
                .WithMany()
                .HasForeignKey(nameof(ITenantOwned.TenantId))
                .OnDelete(DeleteBehavior.Restrict);

            applyFilter.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
        }
    }

    // Tenant query filter: a no-op when filtering is disabled (host/background scope), so the
    // caller sees all tenants. Referencing the injected service instance makes EF re-evaluate
    // the parameters per query rather than baking them into the cached model.
    private void ApplyTenantQueryFilter<TEntity>(ModelBuilder modelBuilder) where TEntity : class, ITenantOwned
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(
            e => !_currentTenant.FilterEnabled || e.TenantId == _currentTenant.FilterTenantId);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTenantOnInsert();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTenantOnInsert();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // When a tenant scope is active, force new tenant-owned rows to that tenant so a tenant
    // admin cannot create data in another tenant. Host/background scope leaves TenantId as set.
    private void StampTenantOnInsert()
    {
        if (!_currentTenant.FilterEnabled)
            return;

        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State == EntityState.Added)
                entry.Entity.TenantId = _currentTenant.FilterTenantId;
        }
    }
}
