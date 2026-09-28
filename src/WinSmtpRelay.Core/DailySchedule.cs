namespace WinSmtpRelay.Core;

/// <summary>
/// Next occurrence of a daily UTC run time ("HH:mm"), measured from a fixed anchor instant.
/// Background loops must compute the target ONCE per wait (anchored at the moment they start waiting)
/// and sleep until it is reached. Re-computing "the next run after now" on every wake-up can never be
/// reached: at the target minute "now" has caught up with it, so the calculation jumps to tomorrow.
/// </summary>
public static class DailySchedule
{
    /// <summary>
    /// The first instant strictly after <paramref name="afterUtc"/> whose time of day is
    /// <paramref name="timeUtc"/>; <paramref name="fallback"/> is used when the value does not parse.
    /// </summary>
    public static DateTime NextRunUtc(string? timeUtc, TimeOnly fallback, DateTime afterUtc)
    {
        if (!TimeOnly.TryParse(timeUtc, out var target))
            target = fallback;

        var todayTarget = afterUtc.Date.Add(target.ToTimeSpan());
        return todayTarget <= afterUtc ? todayTarget.AddDays(1) : todayTarget;
    }
}
