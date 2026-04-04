namespace HomelabBot.Services;

public abstract class ScheduledBackgroundService : BackgroundService
{
    private readonly DiscordBotService _discordBot;

    protected ScheduledBackgroundService(DiscordBotService discordBot)
    {
        _discordBot = discordBot;
    }

    protected DiscordBotService DiscordBot => _discordBot;

    protected abstract bool IsEnabled { get; }

    protected abstract ILogger Logger { get; }

    protected abstract TimeSpan GetDelay();

    protected abstract Task RunIterationAsync(CancellationToken ct);

    protected virtual Task OnStartedAsync(CancellationToken ct) => Task.CompletedTask;

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("{Service} started, waiting for Discord...", GetType().Name);
        await _discordBot.WaitForReadyAsync(stoppingToken);
        Logger.LogInformation("Discord ready, {Service} running", GetType().Name);

        await OnStartedAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!IsEnabled)
                {
                    Logger.LogInformation("{Service} disabled, rechecking in 1 minute", GetType().Name);
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                var delay = GetDelay();
                Logger.LogInformation("{Service} next run in {Delay}", GetType().Name, delay);
                await Task.Delay(delay, stoppingToken);

                await RunIterationAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in {Service}", GetType().Name);
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }
}
