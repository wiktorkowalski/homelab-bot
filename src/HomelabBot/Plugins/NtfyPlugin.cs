using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using HomelabBot.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace HomelabBot.Plugins;

public sealed class NtfyPlugin
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NtfyPlugin> _logger;
    private readonly string _baseUrl;
    private readonly string _defaultTopic;

    public NtfyPlugin(
        IHttpClientFactory httpClientFactory,
        IOptions<NtfyConfiguration> config,
        ILogger<NtfyPlugin> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Default");
        _logger = logger;
        _baseUrl = config.Value.Host.TrimEnd('/');
        _defaultTopic = config.Value.DefaultTopic;
    }

    [KernelFunction]
    [Description("Sends a push notification via ntfy. Can specify topic, title, and priority.")]
    public async Task<string> SendNotification(
        [Description("The notification message content")] string message,
        [Description("Topic to send to (optional, uses default if not specified)")] string? topic = null,
        [Description("Notification title (optional)")] string? title = null,
        [Description("Priority: 1 (min) to 5 (max), default 3")] int priority = 3)
    {
        var targetTopic = topic ?? _defaultTopic;
        _logger.LogInformation("Sending notification to topic {Topic}: {Message}", targetTopic, message);

        try
        {
            var notification = new NtfyMessage
            {
                Topic = targetTopic,
                Message = message,
                Title = title,
                Priority = Math.Clamp(priority, 1, 5)
            };

            var response = await _httpClient.PostAsJsonAsync(_baseUrl, notification);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation("Notification sent to topic {Topic}", targetTopic);

            var priorityName = priority switch
            {
                1 => "min",
                2 => "low",
                3 => "default",
                4 => "high",
                5 => "urgent",
                _ => "default"
            };

            var sb = new StringBuilder();
            sb.AppendLine($"Notification sent to **{targetTopic}**");
            if (!string.IsNullOrEmpty(title))
            {
                sb.AppendLine($"Title: {title}");
            }
            sb.AppendLine($"Priority: {priorityName}");
            sb.AppendLine($"Message: {message}");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending notification to {Topic}", targetTopic);
            return $"Error sending notification: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Sends a notification with action buttons that users can click.")]
    public async Task<string> SendNotificationWithActions(
        [Description("The notification message")] string message,
        [Description("Comma-separated action labels (e.g., 'Open Dashboard,View Logs')")] string actionLabels,
        [Description("Comma-separated action URLs (same count as labels)")] string actionUrls,
        [Description("Topic to send to (optional)")] string? topic = null,
        [Description("Notification title (optional)")] string? title = null)
    {
        var targetTopic = topic ?? _defaultTopic;
        _logger.LogInformation("Sending notification with actions to topic {Topic}", targetTopic);

        try
        {
            var labels = actionLabels.Split(',').Select(l => l.Trim()).ToList();
            var urls = actionUrls.Split(',').Select(u => u.Trim()).ToList();

            if (labels.Count != urls.Count)
            {
                return "Error: Number of action labels must match number of action URLs.";
            }

            var actions = labels.Zip(urls, (label, url) => new NtfyAction
            {
                Action = "view",
                Label = label,
                Url = url
            }).ToList();

            var notification = new NtfyMessageWithActions
            {
                Topic = targetTopic,
                Message = message,
                Title = title,
                Priority = 3,
                Actions = actions
            };

            var response = await _httpClient.PostAsJsonAsync(_baseUrl, notification);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation("Notification with {ActionCount} actions sent to topic {Topic}",
                actions.Count, targetTopic);

            var sb = new StringBuilder();
            sb.AppendLine($"Notification with actions sent to **{targetTopic}**");
            sb.AppendLine($"Actions: {string.Join(", ", labels)}");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending notification with actions to {Topic}", targetTopic);
            return $"Error sending notification: {ex.Message}";
        }
    }

    private sealed class NtfyMessage
    {
        public string Topic { get; set; } = "";
        public string Message { get; set; } = "";
        public string? Title { get; set; }
        public int Priority { get; set; } = 3;
    }

    private sealed class NtfyMessageWithActions
    {
        public string Topic { get; set; } = "";
        public string Message { get; set; } = "";
        public string? Title { get; set; }
        public int Priority { get; set; } = 3;
        public List<NtfyAction> Actions { get; set; } = [];
    }

    private sealed class NtfyAction
    {
        public string Action { get; set; } = "view";
        public string Label { get; set; } = "";
        public string Url { get; set; } = "";
    }
}
