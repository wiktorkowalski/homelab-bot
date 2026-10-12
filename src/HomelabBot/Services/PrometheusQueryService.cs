using System.Net.Http.Json;
using System.Text.Json;
using HomelabBot.Configuration;
using HomelabBot.Models;
using HomelabBot.Models.Prometheus;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class PrometheusQueryService
{
    private const int MaxErrorDetailLength = 500;

    private readonly HttpClient _httpClient;
    private readonly ILogger<PrometheusQueryService> _logger;
    private readonly string _baseUrl;

    public PrometheusQueryService(
        IHttpClientFactory httpClientFactory,
        IOptions<PrometheusConfiguration> config,
        ILogger<PrometheusQueryService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Default");
        _logger = logger;
        _baseUrl = config.Value.Host.TrimEnd('/');
    }

    public async Task<double?> QueryScalarAsync(string query, CancellationToken ct = default)
    {
        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var response = await _httpClient.GetAsync(
                $"{_baseUrl}/api/v1/query?query={encodedQuery}", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Prometheus query failed with {Status}: {Query}",
                    response.StatusCode, query);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<PrometheusQueryResponse>(ct);

            if (result?.Data?.Result?.FirstOrDefault()?.Value?.Length > 1)
            {
                var valueStr = result.Data.Result[0].Value![1].ToString();
                if (double.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query Prometheus: {Query}", query);
            return null;
        }
    }

    // Throws on a fault, unlike the other methods: the caller must tell a rejected query from an empty result.
    public async Task<PrometheusQueryResponse?> QueryAsync(string query, CancellationToken ct = default)
    {
        var encodedQuery = Uri.EscapeDataString(query);
        var response = await _httpClient.GetAsync(
            $"{_baseUrl}/api/v1/query?query={encodedQuery}", ct);
        await EnsureSuccessAsync(response, ct);

        return await response.Content.ReadFromJsonAsync<PrometheusQueryResponse>(ct);
    }

    public async Task<List<MetricResult>> QueryMultipleAsync(string query, CancellationToken ct = default)
    {
        var results = new List<MetricResult>();

        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var response = await _httpClient.GetAsync(
                $"{_baseUrl}/api/v1/query?query={encodedQuery}", ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PrometheusQueryResponse>(ct);
            if (result?.Data?.Result != null)
            {
                foreach (var item in result.Data.Result)
                {
                    var value = 0.0;
                    if (item.Value?.Length > 1)
                    {
                        double.TryParse(item.Value[1].ToString(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out value);
                    }

                    results.Add(new MetricResult
                    {
                        Labels = item.Metric ?? [],
                        Value = value
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query Prometheus multiple: {Query}", query);
        }

        return results;
    }

    public async Task<List<PrometheusTargetInfo>> GetTargetStatusesAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/v1/targets", ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PrometheusTargetsResponse>(ct);
            var targets = result?.Data?.ActiveTargets ?? [];

            return targets.Select(t => new PrometheusTargetInfo
            {
                Job = t.Labels?.GetValueOrDefault("job") ?? "unknown",
                Instance = t.Labels?.GetValueOrDefault("instance") ?? t.ScrapeUrl ?? "unknown",
                Health = t.Health
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get Prometheus targets");
            return [];
        }
    }

    public async Task<PrometheusQueryRangeResponse?> QueryRangeAsync(
        string query, long startUnix, long endUnix, int stepSeconds, CancellationToken ct = default)
    {
        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var response = await _httpClient.GetAsync(
                $"{_baseUrl}/api/v1/query_range?query={encodedQuery}&start={startUnix}&end={endUnix}&step={stepSeconds}", ct);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<PrometheusQueryRangeResponse>(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query Prometheus range: {Query}", query);
            return null;
        }
    }

    // Throws on a fault, like QueryAsync: the caller must tell a Prometheus that is down from an empty list.
    public async Task<PrometheusLabelResponse?> GetLabelValuesAsync(
        string label, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(
            $"{_baseUrl}/api/v1/label/{Uri.EscapeDataString(label)}/values", ct);
        await EnsureSuccessAsync(response, ct);

        return await response.Content.ReadFromJsonAsync<PrometheusLabelResponse>(ct);
    }

    // Prometheus explains a failed request in the "error" field of a JSON body; a proxy in front of it answers with plain text.
    internal static string ExtractErrorDetail(string body)
    {
        var detail = body.Trim();

        try
        {
            using var document = JsonDocument.Parse(detail);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString() is { } reason)
            {
                detail = reason.Trim();
            }
        }
        catch (JsonException)
        {
            // Not JSON: the text itself is the detail.
        }

        return detail.Length == 0 ? "no detail" : ConversationSearchResult.Truncate(detail, MaxErrorDetailLength);
    }

    // EnsureSuccessStatusCode drops the body, and the body is where Prometheus explains a rejected query.
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new PrometheusRequestException(
            response.StatusCode,
            ExtractErrorDetail(await response.Content.ReadAsStringAsync(ct)));
    }
}
