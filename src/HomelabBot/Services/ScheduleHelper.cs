namespace HomelabBot.Services;

public static class ScheduleHelper
{
    public static TimeSpan CalculateDelayUntilNextRun(
        string scheduleTime, string timeZone, DayOfWeek? scheduleDay = null)
    {
        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            tz = TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            tz = TimeZoneInfo.Utc;
        }

        if (!TimeOnly.TryParse(scheduleTime, out var parsedTime))
        {
            parsedTime = new TimeOnly(8, 0);
        }

        var nowUtc = DateTime.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var targetToday = nowLocal.Date + parsedTime.ToTimeSpan();

        if (scheduleDay.HasValue)
        {
            // Weekly scheduling: find next occurrence of the target day
            var daysUntil = ((int)scheduleDay.Value - (int)nowLocal.DayOfWeek + 7) % 7;
            if (daysUntil == 0 && nowLocal >= targetToday)
            {
                daysUntil = 7;
            }

            targetToday = targetToday.AddDays(daysUntil);
        }
        else
        {
            // Daily scheduling: if past target, schedule for tomorrow
            if (nowLocal >= targetToday)
            {
                targetToday = targetToday.AddDays(1);
            }
        }

        // Handle DST invalid times
        if (tz.IsInvalidTime(targetToday))
        {
            targetToday = targetToday.AddHours(1);
        }

        var targetUtc = TimeZoneInfo.ConvertTimeToUtc(targetToday, tz);
        var delay = targetUtc - nowUtc;

        return delay > TimeSpan.Zero ? delay : TimeSpan.FromMinutes(1);
    }
}
