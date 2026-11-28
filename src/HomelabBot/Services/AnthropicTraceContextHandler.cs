using System.Diagnostics;

namespace HomelabBot.Services;

// The API joins its exported spans to the bot's trace by traceparent and copies baggage user.id onto them.
// user.id is <user>:<conversation>: the API takes no conversation id, so the owner's collector splits it into user.id
// and session.id. Set per request, not as Activity baggage: tools run under the turn span, and their HTTP calls
// would carry the ids.
internal sealed class AnthropicTraceContextHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity)
        {
            request.Headers.TryAddWithoutValidation("traceparent", activity.Id);
            if (BaggageUserId(activity) is { } userId)
            {
                request.Headers.TryAddWithoutValidation("baggage", $"user.id={userId}");
            }
        }

        return base.SendAsync(request, cancellationToken);
    }

    // Both ids are Discord snowflakes; the escape keeps any other value from adding a ':' or a baggage delimiter.
    private static string? BaggageUserId(Activity activity)
    {
        if (TelemetryConstants.FindTag(activity, TelemetryConstants.UserIdTag) is not { } userId)
        {
            return null;
        }

        var user = Uri.EscapeDataString(userId);
        return TelemetryConstants.FindTag(activity, TelemetryConstants.SessionIdTag) is { } sessionId
            ? $"{user}:{Uri.EscapeDataString(sessionId)}"
            : user;
    }
}
