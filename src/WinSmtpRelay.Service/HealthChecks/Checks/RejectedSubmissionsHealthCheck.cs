using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Health;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Service.HealthChecks.Checks;

/// <summary>
/// Surfaces devices the relay is refusing. A rejection from a network the operator deliberately
/// configured is not background noise — it is a known device that cannot send, i.e. the relay silently
/// not relaying, which is its worst state.
/// <para>
/// Only trusted sources produce findings. Port 25 accepts connections from wherever the listener is
/// exposed, so untrusted rejections are the relay working exactly as designed; reporting them would bury
/// the real signal and train the operator to ignore this section. They are counted, never alerted on.
/// </para>
/// </summary>
public sealed class RejectedSubmissionsHealthCheck(RelayDbContext db) : HealthCheckBase
{
    public override string Name => "Rejected submissions";
    protected override string Category => HealthCategories.Configuration;

    /// <summary>How recently a row must have occurred to count as still happening (the check runs daily).</summary>
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a rejection must persist before it is a finding rather than noise. A one-off is a wrong
    /// password, a test, a transient; a device still failing a day later is, by definition, configured
    /// wrong and not self-correcting. That is the alert worth having.
    /// </summary>
    private static readonly TimeSpan PersistentThreshold = TimeSpan.FromHours(24);

    /// <summary>Cap on reported rows, so one badly-behaved subnet cannot crowd out the rest of the report.</summary>
    private const int MaxReported = 10;

    public override async Task<IReadOnlyList<HealthFinding>> RunAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now - ActiveWindow;

        // LastSeenUtc is a non-nullable DateTimeOffset stored as a fixed-width UTC ISO string, so this
        // range filter translates on SQLite (see RelayDbContext.ConfigureConventions). The IgnoredUtc
        // null-check is a plain IS NULL, which translates regardless of the converter.
        var active = await db.RejectedSubmissions
            .AsNoTracking()
            .Where(r => r.IsTrustedSource && r.IgnoredUtc == null && r.LastSeenUtc >= since)
            .ToListAsync(ct);

        var findings = new List<HealthFinding>();

        var persistent = active
            .Where(r => r.LastSeenUtc - r.FirstSeenUtc > PersistentThreshold)
            .OrderByDescending(r => r.Count)
            .ToList();

        if (persistent.Count == 0)
        {
            findings.Add(Ok("rejected-persistent", "No configured device is being refused",
                active.Count == 0
                    ? "No submission from a trusted network was refused in the last 24 hours."
                    : $"{active.Count} submission source(s) from a trusted network were refused in the last 24 hours, "
                      + "but none has been failing for longer than a day."));
        }
        else
        {
            foreach (var row in persistent.Take(MaxReported))
                findings.Add(BuildPersistentFinding(row, now));

            if (persistent.Count > MaxReported)
            {
                findings.Add(Info("rejected-persistent-more",
                    $"{persistent.Count - MaxReported} more device(s) are being refused",
                    $"Only the {MaxReported} most frequent are listed above. See the rejected submissions "
                    + "list in the admin UI for the full set."));
            }
        }

        // Recent-but-not-yet-persistent trusted rejections: worth seeing, not worth alerting on.
        var recent = active.Count - persistent.Count;
        if (recent > 0)
        {
            findings.Add(Info("rejected-recent", $"{recent} recent rejection source(s) from a trusted network",
                "These started less than a day ago and may be a wrong password, a test, or a transient. "
                + "They become a warning if they are still failing tomorrow."));
        }

        return findings;
    }

    private HealthFinding BuildPersistentFinding(RejectedSubmission row, DateTimeOffset now)
    {
        var age = now - row.FirstSeenUtc;
        var who = row.SenderDomain.Length > 0
            ? $"{row.ClientIp} sending as {row.SenderDomain}"
            : row.ClientIp;

        var detail =
            $"{who} has been refused since {row.FirstSeenUtc:yyyy-MM-dd HH:mm} UTC "
            + $"({(int)age.TotalHours} h, {row.Count} attempt(s), last {row.LastSeenUtc:yyyy-MM-dd HH:mm} UTC): "
            + $"{row.Reason.Describe()}. The client is inside a configured network, so this is a device that "
            + "is expected to send and cannot.";

        if (!string.IsNullOrWhiteSpace(row.Detail))
            detail += $" [{row.Detail}]";
        if (!string.IsNullOrWhiteSpace(row.LastBuffer))
            detail += $" Last command: {row.LastBuffer}";

        // Code + Target is the cross-run identity used to diff findings, so it must mirror the row's key.
        var target = row.SenderDomain.Length > 0 ? $"{row.ClientIp} ({row.SenderDomain})" : row.ClientIp;

        return Warn(
            $"rejected-{row.Reason}",
            $"A configured device has been refused for over a day: {who}",
            detail,
            target,
            Remediation(row));
    }

    /// <summary>
    /// What the operator should actually do. The relay already knows the client IP, the sender domain,
    /// the tenant rules and the exact gate that refused — so it can name the fix rather than merely
    /// reporting that something went wrong.
    /// </summary>
    private static string Remediation(RejectedSubmission row) => row.Reason switch
    {
        RejectReason.SenderDomainNotAccepted =>
            $"Add {row.SenderDomain} under Accepted Sender Domains if this device should send as it, "
            + $"or correct the device's From address.",
        RejectReason.SenderDomainNotVerified =>
            $"Complete the DNS ownership verification for {row.SenderDomain}, or turn off "
            + "\"require sender domain verification\".",
        RejectReason.RecipientDomainNotVerified =>
            "Complete the DNS ownership verification for the recipient domain, or turn off "
            + "\"require recipient domain verification\".",
        RejectReason.NotInAllowedNetworks or RejectReason.IpBlocked =>
            $"Add an allow rule for {row.ClientIp} under IP Access Rules if this device should be able to send.",
        RejectReason.OpenRelayDenied =>
            $"This device is relaying to an external domain without authentication. Give it SMTP credentials, "
            + $"or add an explicit (non-\"any\") allow-IP rule for {row.ClientIp}.",
        RejectReason.TenantAttributionFailed =>
            $"The relay cannot tell which tenant {row.ClientIp} belongs to. Bind the IP to a tenant with an "
            + "allow rule, or register the sender domain to the owning tenant.",
        RejectReason.TenantDisabled =>
            "The owning tenant is disabled. Re-enable it, or stop the device from sending.",
        RejectReason.IpAutoBanned =>
            $"{row.ClientIp} was auto-banned after repeated failed authentication — the device is almost "
            + "certainly using stale credentials. Fix them, then clear the ban.",
        RejectReason.IpRateLimitExceeded or RejectReason.SenderRateLimitExceeded or RejectReason.UserRateLimitExceeded =>
            "The sender is being throttled, not refused outright: either raise the limit or reduce the "
            + "device's send rate. Note that a throttled sender is currently answered with a permanent 550 "
            + "and will bounce the mail rather than retry.",
        RejectReason.MessageTooLarge =>
            "The device is sending messages above the size limit. Raise the limit or reduce the message size.",
        RejectReason.SpfFail =>
            $"SPF hard-fails for {row.SenderDomain} from {row.ClientIp}. Add this relay to the domain's SPF "
            + "record, or relax SPF enforcement.",
        RejectReason.CommandSyntaxError =>
            "The device is speaking malformed SMTP. The last command line is shown above — compare it with "
            + "what the device is configured to send.",
        _ => "Review this device's configuration against the relay's rules."
    };
}
