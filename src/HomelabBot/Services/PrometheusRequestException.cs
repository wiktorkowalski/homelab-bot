using System.Net;

namespace HomelabBot.Services;

// The message holds the status only: generic catches log it at Error and return it to the caller.
// Detail is the reason from Prometheus. It can quote the query, so only the rejected-query answer uses it.
internal sealed class PrometheusRequestException(HttpStatusCode status, string detail)
    : HttpRequestException($"Prometheus answered {(int)status}", null, status)
{
    public string Detail { get; } = detail;

    // Prometheus answers 400 for a query that does not parse and 422 for one that cannot run.
    public bool IsQueryRejection => StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity;
}
