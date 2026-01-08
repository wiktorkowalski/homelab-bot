using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomelabBot.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace HomelabBot.Plugins;

public sealed class HomeAssistantPlugin
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HomeAssistantPlugin> _logger;
    private readonly string _baseUrl;

    public HomeAssistantPlugin(
        IHttpClientFactory httpClientFactory,
        IOptions<HomeAssistantConfiguration> config,
        ILogger<HomeAssistantPlugin> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Default");
        _logger = logger;
        _baseUrl = config.Value.Host.TrimEnd('/');

        if (!string.IsNullOrEmpty(config.Value.AccessToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.Value.AccessToken);
        }
    }

    [KernelFunction]
    [Description("Gets the current state of a Home Assistant entity by its entity_id.")]
    public async Task<string> GetEntityState([Description("Entity ID (e.g., sensor.temperature, light.living_room)")] string entityId)
    {
        _logger.LogDebug("Getting state for entity {EntityId}...", entityId);

        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/states/{entityId}");

            if (!response.IsSuccessStatusCode)
            {
                return $"Entity '{entityId}' not found or inaccessible.";
            }

            var state = await response.Content.ReadFromJsonAsync<HAState>();

            if (state == null)
            {
                return $"Could not retrieve state for '{entityId}'.";
            }

            var sb = new StringBuilder();
            var friendlyName = GetAttributeString(state.Attributes, "friendly_name") ?? entityId;

            sb.AppendLine($"**{friendlyName}**");
            sb.AppendLine($"Entity ID: `{state.EntityId}`");
            sb.AppendLine($"State: **{state.State}**");

            // Show relevant attributes
            if (state.Attributes != null)
            {
                var unitOfMeasurement = GetAttributeString(state.Attributes, "unit_of_measurement");
                if (!string.IsNullOrEmpty(unitOfMeasurement))
                {
                    sb.AppendLine($"Unit: {unitOfMeasurement}");
                }

                var deviceClass = GetAttributeString(state.Attributes, "device_class");
                if (!string.IsNullOrEmpty(deviceClass))
                {
                    sb.AppendLine($"Device Class: {deviceClass}");
                }
            }

            sb.AppendLine($"Last Updated: {state.LastUpdated:g}");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting entity state for {EntityId}", entityId);
            return $"Error getting entity state: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Lists all entities in a specific Home Assistant domain (e.g., light, switch, sensor).")]
    public async Task<string> ListEntities([Description("Domain to list (e.g., light, switch, sensor, automation)")] string domain)
    {
        _logger.LogDebug("Listing entities for domain {Domain}...", domain);

        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/states");
            response.EnsureSuccessStatusCode();

            var states = await response.Content.ReadFromJsonAsync<List<HAState>>();

            if (states == null)
            {
                return "Could not retrieve entities.";
            }

            var filtered = states
                .Where(s => s.EntityId?.StartsWith(domain + ".") == true)
                .OrderBy(s => GetAttributeString(s.Attributes, "friendly_name") ?? s.EntityId)
                .ToList();

            if (filtered.Count == 0)
            {
                return $"No entities found in domain '{domain}'.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"**{domain.ToUpperInvariant()} Entities ({filtered.Count})**\n");

            foreach (var entity in filtered.Take(30))
            {
                var friendlyName = GetAttributeString(entity.Attributes, "friendly_name") ?? "";
                var stateEmoji = entity.State switch
                {
                    "on" => "🟢",
                    "off" => "⚫",
                    "unavailable" => "❌",
                    "unknown" => "❓",
                    _ => "⚪"
                };

                var displayState = entity.State ?? "unknown";
                var unit = GetAttributeString(entity.Attributes, "unit_of_measurement");
                if (!string.IsNullOrEmpty(unit))
                {
                    displayState += $" {unit}";
                }

                sb.AppendLine($"{stateEmoji} **{friendlyName}** - {displayState}");
                sb.AppendLine($"   `{entity.EntityId}`");
            }

            if (filtered.Count > 30)
            {
                sb.AppendLine($"\n... and {filtered.Count - 30} more entities");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing entities for domain {Domain}", domain);
            return $"Error listing entities: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Turns on a Home Assistant entity (light, switch, etc.).")]
    public async Task<string> TurnOn([Description("Entity ID to turn on")] string entityId)
    {
        _logger.LogInformation("Turning on {EntityId}...", entityId);

        try
        {
            var domain = entityId.Split('.')[0];
            var response = await _httpClient.PostAsJsonAsync(
                $"{_baseUrl}/api/services/{domain}/turn_on",
                new { entity_id = entityId });

            response.EnsureSuccessStatusCode();

            _logger.LogInformation("Turned on {EntityId}", entityId);
            return $"Turned on **{entityId}**.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error turning on {EntityId}", entityId);
            return $"Error turning on entity: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Turns off a Home Assistant entity (light, switch, etc.).")]
    public async Task<string> TurnOff([Description("Entity ID to turn off")] string entityId)
    {
        _logger.LogInformation("Turning off {EntityId}...", entityId);

        try
        {
            var domain = entityId.Split('.')[0];
            var response = await _httpClient.PostAsJsonAsync(
                $"{_baseUrl}/api/services/{domain}/turn_off",
                new { entity_id = entityId });

            response.EnsureSuccessStatusCode();

            _logger.LogInformation("Turned off {EntityId}", entityId);
            return $"Turned off **{entityId}**.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error turning off {EntityId}", entityId);
            return $"Error turning off entity: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Triggers a Home Assistant automation.")]
    public async Task<string> TriggerAutomation([Description("Automation entity ID (e.g., automation.morning_routine)")] string automationId)
    {
        _logger.LogInformation("Triggering automation {AutomationId}...", automationId);

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{_baseUrl}/api/services/automation/trigger",
                new { entity_id = automationId });

            response.EnsureSuccessStatusCode();

            _logger.LogInformation("Triggered automation {AutomationId}", automationId);
            return $"Triggered automation **{automationId}**.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering automation {AutomationId}", automationId);
            return $"Error triggering automation: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Lists all available Home Assistant automations.")]
    public async Task<string> ListAutomations()
    {
        _logger.LogDebug("Listing automations...");

        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/states");
            response.EnsureSuccessStatusCode();

            var states = await response.Content.ReadFromJsonAsync<List<HAState>>();

            var automations = states?
                .Where(s => s.EntityId?.StartsWith("automation.") == true)
                .OrderBy(s => GetAttributeString(s.Attributes, "friendly_name"))
                .ToList();

            if (automations == null || automations.Count == 0)
            {
                return "No automations found.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"**Automations ({automations.Count})**\n");

            foreach (var auto in automations)
            {
                var friendlyName = GetAttributeString(auto.Attributes, "friendly_name") ?? auto.EntityId;
                var stateEmoji = auto.State == "on" ? "🟢" : "⚫";
                var lastTriggered = GetAttributeString(auto.Attributes, "last_triggered");

                sb.AppendLine($"{stateEmoji} **{friendlyName}**");
                sb.AppendLine($"   `{auto.EntityId}`");
                if (!string.IsNullOrEmpty(lastTriggered) && lastTriggered != "None")
                {
                    sb.AppendLine($"   Last triggered: {lastTriggered}");
                }
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing automations");
            return $"Error listing automations: {ex.Message}";
        }
    }

    private static string? GetAttributeString(Dictionary<string, JsonElement>? attributes, string key)
    {
        if (attributes == null || !attributes.TryGetValue(key, out var element))
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
    }

    private sealed class HAState
    {
        [JsonPropertyName("entity_id")]
        public string? EntityId { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("attributes")]
        public Dictionary<string, JsonElement>? Attributes { get; set; }

        [JsonPropertyName("last_updated")]
        public DateTime LastUpdated { get; set; }
    }
}
