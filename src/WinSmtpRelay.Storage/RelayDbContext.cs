using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.Storage;

public class RelayDbContext(DbContextOptions<RelayDbContext> options)
    : IdentityDbContext<AdminUser, AdminRole, int>(options)
{
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
    public DbSet<HeaderRewriteEntry> HeaderRewriteEntries => Set<HeaderRewriteEntry>();
    public DbSet<SenderRewriteEntry> SenderRewriteEntries => Set<SenderRewriteEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // Identity tables (AspNetUsers, AspNetRoles, ...)

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Slug).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.Slug).HasMaxLength(100);
            entity.HasData(new Tenant
            {
                Id = TenantDefaults.DefaultTenantId,
                Name = TenantDefaults.DefaultName,
                Slug = TenantDefaults.DefaultSlug,
                IsEnabled = true,
                CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
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
            entity.HasIndex(e => e.Username).IsUnique();
            entity.Property(e => e.Username).HasMaxLength(255);
        });

        modelBuilder.Entity<DailyStatistics>(entity =>
        {
            entity.HasKey(e => e.Date);
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
            entity.HasIndex(e => e.Domain).IsUnique();
            entity.Property(e => e.Domain).HasMaxLength(255);
        });

        modelBuilder.Entity<AcceptedSenderDomain>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Domain).IsUnique();
            entity.Property(e => e.Domain).HasMaxLength(255);
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
            entity.HasIndex(e => e.Domain).IsUnique();
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
                UpdatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
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
    }
}
