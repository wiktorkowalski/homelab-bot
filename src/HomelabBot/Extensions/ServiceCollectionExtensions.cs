using System.Net;
using HomelabBot.Helpers;
using HomelabBot.Plugins;
using Microsoft.Extensions.Http.Resilience;
using Polly.Timeout;

namespace HomelabBot.Extensions;

internal static class ServiceCollectionExtensions
{
    // Room for one new attempt after a fast fault, e.g. a 503 while Loki restarts.
    private static readonly TimeSpan RetryAllowance = TimeSpan.FromSeconds(15);

    // Loki scans the whole window of a query: a 7d error count takes about 20s, over the 10s attempt limit
    // of the "Default" client. Tests call this too, so they run the wiring of Program.cs.
    public static IServiceCollection AddLokiHttpClient(this IServiceCollection services)
    {
        services.AddHttpClient(LokiPlugin.HttpClientName)
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = LogQl.QueryTimeout;
                options.TotalRequestTimeout.Timeout = LogQl.QueryTimeout + RetryAllowance;

                // Polly rejects a sampling window shorter than two attempt timeouts.
                options.CircuitBreaker.SamplingDuration = LogQl.QueryTimeout * 2;

                // A scan that timed out times out again, here or in Loki (408, 504): only a fault that a new attempt can clear is retried.
                options.Retry.ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is not TimeoutRejectedException
                    && args.Outcome.Result?.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
                    && HttpClientResiliencePredicates.IsTransient(args.Outcome));
            });

        return services;
    }
}
