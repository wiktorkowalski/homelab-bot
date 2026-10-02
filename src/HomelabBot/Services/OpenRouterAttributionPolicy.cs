using System.ClientModel.Primitives;

namespace HomelabBot.Services;

// Pipeline policy, not a custom HttpClient: SK disables SDK retries and timeout when given an HttpClient.
internal sealed class OpenRouterAttributionPolicy : PipelinePolicy
{
    private const string Referer = "https://github.com/wiktorkowalski/homelab-bot";
    private const string Title = "homelab-bot";

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        AddHeaders(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        AddHeaders(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    private static void AddHeaders(PipelineMessage message)
    {
        message.Request.Headers.Set("HTTP-Referer", Referer);
        message.Request.Headers.Set("X-Title", Title);
    }
}
