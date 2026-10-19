using System.Globalization;

namespace WinSmtpRelay.Core;

/// <summary>
/// The time zone the daily schedules (report, self-check) are entered and shown in. Blank = the server's own
/// time zone; otherwise an IANA id such as <c>Europe/Zurich</c> (a Windows id such as
/// <c>W. Europe Standard Time</c> is accepted too). Shown as an IANA id wherever possible.
/// </summary>
public static class ScheduleTimeZone
{
    /// <summary>The zone for <paramref name="id"/>; the server's zone when blank or unknown.</summary>
    public static TimeZoneInfo Resolve(string? id) => TryFind(id, out var zone) ? zone : TimeZoneInfo.Local;

    /// <summary>True when <paramref name="id"/> is blank (server zone) or a known IANA/Windows zone id.</summary>
    public static bool IsValid(string? id) => string.IsNullOrWhiteSpace(id) || TryFind(id, out _);

    /// <summary>Looks up a non-blank IANA or Windows zone id.</summary>
    public static bool TryFind(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id))
            return false;
        try
        {
            // Accepts both IANA and Windows ids on every platform (converted via ICU on Windows).
            zone = TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>
    /// The IANA id of <paramref name="zone"/> (e.g. <c>Europe/Zurich</c>), for labels such as "06:00 (Europe/Zurich)".
    /// A Windows zone covers several IANA zones; the one for the server's region is preferred (W. Europe Standard
    /// Time → Europe/Zurich on a Swiss/Liechtenstein server), else the zone's primary IANA id, else the Windows id.
    /// </summary>
    public static string DisplayId(TimeZoneInfo zone)
    {
        if (zone.HasIanaId)
            return zone.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, RegionCode(), out var regional))
            return regional;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;
    }

    private static string? RegionCode()
    {
        try { return RegionInfo.CurrentRegion.TwoLetterISORegionName; }
        catch (ArgumentException) { return null; }
    }
}
