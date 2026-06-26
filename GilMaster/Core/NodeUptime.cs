using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;

namespace GilMaster.Core;

/// <summary>
/// Timed gathering-node uptime, ported from GatherBuddy's BitfieldUptime.
///
/// Unspoiled/ephemeral nodes are only active during fixed Eorzea-time windows. The
/// game stores these in the GatheringRarePopTimeTable sheet as (StartTime, Duration)
/// pairs in HHMM Eorzea time. We fold those into a 24-bit mask (one bit per ET hour),
/// then read the real clock to report whether a node is up right now and when it next
/// changes — exactly what GatherBuddy shows, with no external uptime tracker needed.
/// </summary>
public static class NodeUptime
{
    private const int  HoursPerDay              = 24;
    private const long RealSecondsPerEorzeaHour = 175; // 175 real seconds == 1 Eorzea hour
    private const uint AllHours                 = 0x00FFFFFF;

    /// <summary>Build the 24-hour uptime mask from a rare-pop table. 0 = no restriction.</summary>
    public static uint FromRarePopTable(GatheringRarePopTimeTable table)
    {
        uint hours = 0;
        var durations = table.Duration;
        var starts    = table.StartTime;
        var count     = Math.Min(durations.Count, starts.Count);
        for (var i = 0; i < count; i++)
        {
            var durationBase = durations[i];
            if (durationBase == 0) continue;

            // 160 is a known data quirk that actually means a 2-hour (0200) window.
            var duration = durationBase == 160 ? (ushort)200 : durationBase;
            var start    = starts[i];
            var end      = (ushort)((start + duration) % 2400);
            hours |= FromEphemeral(start, end);
        }
        return hours;
    }

    // Convert an HHMM start/end window (e.g. 800–1100) to an hour bitmask.
    private static uint FromEphemeral(ushort start, ushort end)
    {
        if (start == end || start > 2400 || end > 2400) return 0; // up at all times → no restriction
        uint ret = 0;
        int s = start / 100, e = end / 100;
        if (e < s) e += HoursPerDay;
        for (var h = s; h < e; h++) ret |= 1u << (h % HoursPerDay);
        return ret;
    }

    public static bool IsAlwaysUp(uint mask) => mask == 0 || mask == AllHours;

    private static bool IsHourUp(uint mask, int hour) => ((mask >> (hour % HoursPerDay)) & 1) == 1;

    /// <summary>Human-readable ET windows, e.g. "00:00–02:00, 12:00–14:00 ET".</summary>
    public static string Windows(uint mask)
    {
        if (IsAlwaysUp(mask)) return "Always up";

        var parts = new List<string>();
        var h = 0;
        while (h < HoursPerDay)
        {
            if (IsHourUp(mask, h))
            {
                var start = h;
                while (h < HoursPerDay && IsHourUp(mask, h)) h++;
                parts.Add($"{start:D2}:00–{h % HoursPerDay:D2}:00");
            }
            else
            {
                h++;
            }
        }
        return parts.Count > 0 ? string.Join(", ", parts) + " ET" : "Always up";
    }

    /// <summary>
    /// Live status from the real clock: whether the node is up now, and the real
    /// minutes until it next flips state (up→down or down→up).
    /// </summary>
    public static (bool IsUp, int MinutesToChange) LiveStatus(uint mask)
    {
        if (IsAlwaysUp(mask)) return (true, int.MaxValue);

        var unix        = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var etHour      = (int)((unix / RealSecondsPerEorzeaHour) % HoursPerDay);
        var secIntoHour = (int)(unix % RealSecondsPerEorzeaHour);

        var upNow = IsHourUp(mask, etHour);

        // How many consecutive ET hours stay in the current state, starting at etHour.
        var hoursInState = 1;
        while (hoursInState < HoursPerDay && IsHourUp(mask, etHour + hoursInState) == upNow)
            hoursInState++;

        // The flip happens at the start of the (etHour + hoursInState) ET hour; we've
        // already consumed secIntoHour of the first hour.
        var realSecondsToFlip = hoursInState * RealSecondsPerEorzeaHour - secIntoHour;
        var minutes = (int)Math.Ceiling(realSecondsToFlip / 60.0);
        return (upNow, minutes);
    }

    /// <summary>Compact live label for the UI, e.g. "UP · 4m left" or "in 1h12m".</summary>
    public static string LiveLabel(uint mask)
    {
        var (up, mins) = LiveStatus(mask);
        if (mins == int.MaxValue) return "Always up";
        var t = mins >= 60 ? $"{mins / 60}h{mins % 60:D2}m" : $"{mins}m";
        return up ? $"UP · {t} left" : $"up in {t}";
    }
}
