namespace WinSmtpRelay.Core;

/// <summary>
/// Occurrences of a daily run time ("HH:mm"), measured from a fixed anchor instant. The time is a LOCAL time of
/// day in a time zone (UTC is just one zone): "06:00 Europe/Zurich" stays 06:00 on the wall clock across the
/// daylight-saving changes, so its UTC instant moves (04:00 UTC in summer, 05:00 UTC in winter). Every result is
/// a UTC instant.
/// <para>
/// Background loops must compute the target ONCE per wait (anchored at the moment they start waiting) and sleep
/// until it is reached. Re-computing "the next run after now" on every wake-up can never be reached: at the
/// target minute "now" has caught up with it, so the calculation jumps to tomorrow.
/// </para>
/// <para>
/// Daylight-saving edges: a time inside the spring-forward gap (e.g. 02:30 on the last Sunday of March in
/// Europe/Zurich, which does not exist) runs at the first valid instant after the gap (03:00 local); a time
/// inside the autumn overlap (02:30 on the last Sunday of October, which happens twice) runs once, at its first
/// occurrence (still on summer time).
/// </para>
/// </summary>
public static class DailySchedule
{
    /// <summary>
    /// The first instant strictly after <paramref name="afterUtc"/> whose UTC time of day is
    /// <paramref name="timeUtc"/>; <paramref name="fallback"/> is used when the value does not parse.
    /// </summary>
    public static DateTime NextRunUtc(string? timeUtc, TimeOnly fallback, DateTime afterUtc)
        => NextRunUtc(timeUtc, fallback, TimeZoneInfo.Utc, afterUtc);

    /// <summary>
    /// The first instant strictly after <paramref name="afterUtc"/> at which the wall clock in
    /// <paramref name="zone"/> shows <paramref name="localTime"/> (DST-safe, see the class remarks);
    /// <paramref name="fallback"/> is used when the value does not parse.
    /// </summary>
    public static DateTime NextRunUtc(string? localTime, TimeOnly fallback, TimeZoneInfo zone, DateTime afterUtc)
    {
        var target = ParseOrDefault(localTime, fallback);
        var after = AsUtc(afterUtc);

        // Start one local day early: the occurrence on the previous local date can still lie after the anchor
        // when a gap moved it. Four local dates always contain the answer (one occurrence per local date).
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(after, zone)).AddDays(-1);
        for (var i = 0; i < 4; i++, localDate = localDate.AddDays(1))
        {
            var run = OccurrenceUtc(localDate, target, zone);
            if (run > after)
                return run;
        }
        return OccurrenceUtc(localDate, target, zone);
    }

    /// <summary>
    /// The latest instant at or before <paramref name="atOrBeforeUtc"/> at which the wall clock in
    /// <paramref name="zone"/> showed <paramref name="localTime"/> — the run that is due "now".
    /// </summary>
    public static DateTime PreviousRunUtc(string? localTime, TimeOnly fallback, TimeZoneInfo zone, DateTime atOrBeforeUtc)
    {
        var target = ParseOrDefault(localTime, fallback);
        var at = AsUtc(atOrBeforeUtc);

        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(at, zone)).AddDays(1);
        for (var i = 0; i < 4; i++, localDate = localDate.AddDays(-1))
        {
            var run = OccurrenceUtc(localDate, target, zone);
            if (run <= at)
                return run;
        }
        return OccurrenceUtc(localDate, target, zone);
    }

    /// <summary>
    /// The UTC instant of <paramref name="time"/> on the local date <paramref name="localDate"/> in
    /// <paramref name="zone"/>: a time in the spring-forward gap maps to the end of the gap, a time in the
    /// autumn overlap to its first occurrence.
    /// </summary>
    public static DateTime OccurrenceUtc(DateOnly localDate, TimeOnly time, TimeZoneInfo zone)
    {
        var local = localDate.ToDateTime(time, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(local))
        {
            // Gap: the wall clock jumps over this time. Walk forward to the first local time that exists — the
            // instant the clock jumps to (gaps start and end on whole minutes, and are at most a few hours long).
            var probe = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);
            while (zone.IsInvalidTime(probe))
                probe = probe.AddMinutes(1);
            return TimeZoneInfo.ConvertTimeToUtc(probe, zone);
        }

        if (zone.IsAmbiguousTime(local))
        {
            // Overlap: the time happens twice. The first occurrence is the one with the larger UTC offset
            // (still on daylight-saving time), i.e. the earlier UTC instant.
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>Parses "HH:mm" (or any <see cref="TimeOnly"/> text); <paramref name="fallback"/> when blank or invalid.</summary>
    public static TimeOnly ParseOrDefault(string? time, TimeOnly fallback)
        => TryParseTime(time, out var t) ? t : fallback;

    /// <summary>Parses a time of day such as "06:00" or "6:30" (culture-invariant).</summary>
    public static bool TryParseTime(string? time, out TimeOnly value)
    {
        value = default;
        return !string.IsNullOrWhiteSpace(time)
            && TimeOnly.TryParse(time.Trim(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out value);
    }

    /// <summary>"HH:mm" — the stored and displayed form of a schedule time.</summary>
    public static string Format(TimeOnly time) => time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
