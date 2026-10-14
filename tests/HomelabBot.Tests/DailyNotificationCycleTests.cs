using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomelabBot.Tests;

public class DailyNotificationCycleTests
{
    [Fact]
    public void FreshServiceStartsInTodaysCycle()
    {
        // A rotation here would clear history through the null ConversationService and throw.
        var service = new SmartNotificationService(null!, null!, null!, null!, NullLogger<SmartNotificationService>.Instance);
        var threadId = service.CurrentDailyThreadId;

        service.StartNewCycle();

        Assert.Equal(threadId, service.CurrentDailyThreadId);
    }
}
