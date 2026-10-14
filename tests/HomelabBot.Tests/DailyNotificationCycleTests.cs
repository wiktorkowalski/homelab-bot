using HomelabBot.Configuration;
using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomelabBot.Tests;

public class DailyNotificationCycleTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(-2)]
    public void FreshService_ContinuesTheCycleOfTheLastDailyRun(int hoursToScheduledRun)
    {
        var scheduledRunUtc = DateTime.UtcNow.AddHours(hoursToScheduledRun);
        var lastRunUtc = hoursToScheduledRun > 0 ? scheduledRunUtc.AddDays(-1) : scheduledRunUtc;

        var service = CreateService(scheduledRunUtc.ToString("HH:mm"));

        Assert.Equal(
            SmartNotificationService.GenerateDailyThreadId(lastRunUtc.ToString("yyyy-MM-dd")),
            service.CurrentDailyThreadId);
    }

    private static SmartNotificationService CreateService(string scheduleTimeUtc)
    {
        var config = Substitute.For<IOptionsMonitor<DailySummaryConfiguration>>();
        config.CurrentValue.Returns(new DailySummaryConfiguration { ScheduleTime = scheduleTimeUtc, TimeZone = "UTC" });

        return new SmartNotificationService(null!, null!, null!, null!, config, NullLogger<SmartNotificationService>.Instance);
    }
}
