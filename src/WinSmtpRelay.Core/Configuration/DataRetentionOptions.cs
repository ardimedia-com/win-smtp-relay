namespace WinSmtpRelay.Core.Configuration;

/// <summary>
/// Initial data-retention values, bound from the appsettings <c>DataRetention</c> section. Seeded into the
/// database on first run (when the row has never been edited); after that the database is authoritative.
/// Defaults match the "Standard" (GDPR-balanced) profile.
/// </summary>
public class DataRetentionOptions
{
    public const string SectionName = "DataRetention";

    public string Profile { get; set; } = "Standard";
    public bool StripBodyOnDelivery { get; set; } = true;
    public int MessageHistoryDays { get; set; } = 30;
    public int DeliveryLogDays { get; set; } = 90;
    public int SuppressionDays { get; set; }
    public int ResendRetentionDays { get; set; } = 7;
}
