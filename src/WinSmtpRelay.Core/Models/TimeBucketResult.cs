namespace WinSmtpRelay.Core.Models;

/// <param name="Label">UTC-formatted label; also the queue-depth join key on the live chart.</param>
/// <param name="TimestampUtc">The bucket's instant in UTC, so the UI can format it in the viewer's local time.</param>
public record TimeBucketResult(string Label, int Sent, int Failed, DateTime TimestampUtc);
