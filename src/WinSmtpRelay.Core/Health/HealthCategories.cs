namespace WinSmtpRelay.Core.Health;

/// <summary>
/// Stable category keys for self-check findings. Kept as constants (not an enum) so a finding's category
/// round-trips to the database as readable text and new categories can be added without a migration. The
/// admin UI groups findings by these and renders them in <see cref="Display"/> order.
/// </summary>
public static class HealthCategories
{
    public const string Deliverability = "Deliverability";
    public const string Connectivity = "Connectivity";
    public const string Configuration = "Configuration";
    public const string Certificates = "Certificates";
    public const string Queue = "Queue";
    public const string Security = "Security";
    public const string Runtime = "Runtime";

    /// <summary>Display order for the categories on the self-check page and in the digest.</summary>
    public static readonly IReadOnlyList<string> Display =
    [
        Deliverability, Connectivity, Certificates, Configuration, Queue, Security, Runtime,
    ];
}
