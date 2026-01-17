using System.Diagnostics;
using System.Text.Json;
using Microsoft.SemanticKernel;

namespace HomelabBot.Services;

public sealed class TelemetryFunctionFilter : IFunctionInvocationFilter
{
    private readonly TelemetryService _telemetryService;
    private readonly ILogger<TelemetryFunctionFilter> _logger;

    public TelemetryFunctionFilter(TelemetryService telemetryService, ILogger<TelemetryFunctionFilter> logger)
    {
        _telemetryService = telemetryService;
        _logger = logger;
    }

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        var interactionId = _telemetryService.ActiveInteractionId;
        if (interactionId is null)
        {
            // No active interaction, just execute
            await next(context);
            return;
        }

        var sw = Stopwatch.StartNew();
        var pluginName = context.Function.PluginName ?? "Unknown";
        var functionName = context.Function.Name;

        string? argumentsJson = null;
        if (context.Arguments.Count > 0)
        {
            try
            {
                argumentsJson = JsonSerializer.Serialize(
                    context.Arguments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString()));
            }
            catch
            {
                // Ignore serialization errors
            }
        }

        try
        {
            await next(context);
            sw.Stop();

            string? resultJson = null;
            var resultValue = context.Result?.GetValue<object>();
            if (resultValue is not null)
            {
                try
                {
                    resultJson = resultValue is string str
                        ? str
                        : JsonSerializer.Serialize(resultValue);
                }
                catch
                {
                    resultJson = resultValue.ToString();
                }
            }

            await _telemetryService.LogToolCallAsync(
                interactionId.Value,
                pluginName,
                functionName,
                argumentsJson,
                resultJson,
                success: true,
                errorMessage: null,
                sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();

            await _telemetryService.LogToolCallAsync(
                interactionId.Value,
                pluginName,
                functionName,
                argumentsJson,
                resultJson: null,
                success: false,
                ex.Message,
                sw.ElapsedMilliseconds);

            throw;
        }
    }
}
