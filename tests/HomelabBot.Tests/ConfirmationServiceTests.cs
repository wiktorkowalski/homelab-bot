using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomelabBot.Tests;

// The answer rules of a confirmation button, without Discord.
public sealed class ConfirmationServiceTests
{
    private const ulong Owner = 1;
    private const ulong Stranger = 2;

    private readonly ConfirmationService _service = new(NullLogger<ConfirmationService>.Instance, TimeSpan.FromMinutes(1));

    [Fact]
    public async Task ClickOfAnotherUser_DoesNotDecide_AndTheOwnersClickStillCounts()
    {
        var pending = _service.Register(Owner, "Docker.RestartContainer(name: plex)");

        var (strangerClick, _) = _service.Answer(pending.Id, Stranger, approve: true);
        Assert.Equal(ConfirmationClick.NotYours, strangerClick);
        Assert.False(pending.Decision.Task.IsCompleted);

        var (ownerClick, _) = _service.Answer(pending.Id, Owner, approve: false);
        Assert.Equal(ConfirmationClick.Answered, ownerClick);
        Assert.Equal(ToolConfirmationDecision.Declined, await _service.WaitAsync(pending, CancellationToken.None));
    }

    [Fact]
    public async Task SecondClick_DoesNothing()
    {
        var pending = _service.Register(Owner, "action");

        Assert.Equal(ConfirmationClick.Answered, _service.Answer(pending.Id, Owner, approve: true).Click);
        Assert.Equal(ConfirmationClick.Unknown, _service.Answer(pending.Id, Owner, approve: false).Click);
        Assert.Equal(ToolConfirmationDecision.Approved, await _service.WaitAsync(pending, CancellationToken.None));
    }

    [Fact]
    public async Task NoAnswerInTime_TimesOut_AndALateClickDoesNothing()
    {
        var service = new ConfirmationService(NullLogger<ConfirmationService>.Instance, TimeSpan.FromMilliseconds(20));
        var pending = service.Register(Owner, "action");

        Assert.Equal(ToolConfirmationDecision.TimedOut, await service.WaitAsync(pending, CancellationToken.None));
        Assert.Equal(ConfirmationClick.Unknown, service.Answer(pending.Id, Owner, approve: true).Click);
    }

    [Fact]
    public async Task CancelledTurn_StopsWaiting_AndALateClickDoesNothing()
    {
        var pending = _service.Register(Owner, "action");
        using var cts = new CancellationTokenSource();
        var wait = _service.WaitAsync(pending, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(ConfirmationClick.Unknown, _service.Answer(pending.Id, Owner, approve: true).Click);
    }

    [Fact]
    public void UnknownId_IsUnknown()
    {
        Assert.Equal(ConfirmationClick.Unknown, _service.Answer("nope", Owner, approve: true).Click);
    }
}
