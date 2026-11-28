using System.Diagnostics;

namespace HomelabBot.Services;

// The API joins its exported spans to the bot's trace by traceparent and names the end user by baggage user.id.
// Set per request, not as Activity baggage: tools run under the turn span, and their HTTP calls would carry the id.
internal sealed class AnthropicTraceContextHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity)
        {
            request.Headers.TryAddWithoutValidation("traceparent", activity.Id);
            if (TelemetryConstants.FindTag(activity, TelemetryConstants.UserIdTag) is { } userId)
            {
                request.Headers.TryAddWithoutValidation("baggage", $"user.id={Uri.EscapeDataString(userId)}");
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
