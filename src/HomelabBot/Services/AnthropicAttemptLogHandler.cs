using System.Diagnostics;

namespace HomelabBot.Services;

// The SDK retries 408/409/429/5xx, connection errors and attempt timeouts without a log line.
// This handler sits inside the SDK retry loop (ClientOptions.Handlers), so it sees every attempt.
// It reads status and headers only; the SDK reads the body itself after the headers arrive.
internal sealed class AnthropicAttemptLogHandler : DelegatingHandler
{
    private const string RetryCountHeader = "x-stainless-retry-count";
    private const string RequestIdHeader = "request-id";
    private const string RetryAfterMsHeader = "retry-after-ms";
    private const string RetryAfterHeader = "retry-after";

    private readonly ILogger _logger;

    public AnthropicAttemptLogHandler(ILogger logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempt = Attempt(request);
        var started = Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // The message only: a stack trace per attempt is noise, the final failure logs one.
            _logger.LogWarning(
                "Claude API attempt {Attempt} got no response after {ElapsedMs} ms: {Error}",
                attempt,
                ElapsedMs(started),
                ex.Message);
            throw;
        }
        catch (OperationCanceledException)
        {
            // The handler sees the SDK linked token only, so it cannot tell an attempt timeout from a caller cancel.
            _logger.LogWarning(
                "Claude API attempt {Attempt} got no response after {ElapsedMs} ms: timeout or cancel",
                attempt,
                ElapsedMs(started));
            throw;
        }

        // The int, not the enum: 529 (overloaded) has no HttpStatusCode name.
        var status = (int)response.StatusCode;

        // Same status set as the SDK retry rule. 4xx outside it are logged with the body by KernelService.
        if (status is 408 or 409 or 429 or >= 500)
        {
            _logger.LogWarning(
                "Claude API attempt {Attempt} failed with status {Status} after {ElapsedMs} ms, request id {RequestId}, retry-after {RetryAfter}",
                attempt,
                status,
                ElapsedMs(started),
                Header(response, RequestIdHeader),
                RetryAfter(response));
        }

        return response;
    }

    // The SDK sets 0, 1, 2 on its own attempts; a missing header counts as the first attempt.
    private static int Attempt(HttpRequestMessage request) =>
        request.Headers.TryGetValues(RetryCountHeader, out var values) && int.TryParse(values.FirstOrDefault(), out var retries)
            ? retries + 1
            : 1;

    private static string RetryAfter(HttpResponseMessage response) =>
        Header(response, RetryAfterMsHeader) is { } ms ? $"{ms} ms" : Header(response, RetryAfterHeader) ?? "none";

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
