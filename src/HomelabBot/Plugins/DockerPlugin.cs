using System.ComponentModel;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using HomelabBot.Models;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Server;

namespace HomelabBot.Plugins;

[McpServerToolType]
public class DockerPlugin
{
    private readonly DockerClient _client;
    private readonly ILogger<DockerPlugin> _logger;

    public DockerPlugin(DockerClient client, ILogger<DockerPlugin> logger)
    {
        _client = client;
        _logger = logger;
    }

    [KernelFunction]
    [McpServerTool]
    [Description("Lists all Docker containers with their status. Returns container name, status, and image.")]
    public async Task<string> ListContainers()
    {
        _logger.LogDebug("Listing Docker containers...");

        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true
        });

        if (containers.Count == 0)
        {
            return "No containers found.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {containers.Count} containers:\n");

        foreach (var container in containers.OrderBy(c => c.Names.FirstOrDefault()))
        {
            var name = container.Names.FirstOrDefault()?.TrimStart('/') ?? container.ID[..12];
            var status = container.State;
            var statusDetail = container.Status;
            var image = container.Image;

            var emoji = status switch
            {
                "running" => "🟢",
                "exited" => "🔴",
                "paused" => "🟡",
                "restarting" => "🔄",
                _ => "⚪"
            };

            sb.AppendLine($"{emoji} **{name}** - {statusDetail}");
            sb.AppendLine($"   Image: {image}");
        }

        return sb.ToString();
    }

    [KernelFunction]
    [McpServerTool]
    [Description("Gets detailed status information for a specific container by name or ID.")]
    public virtual async Task<string> GetContainerStatus([Description("Container name or ID")] string containerName)
    {
        _logger.LogDebug("Getting status for container {Container}...", containerName);

        var container = await FindContainerAsync(containerName);
        if (container == null)
        {
            return $"Container '{containerName}' not found.";
        }

        var inspect = await _client.Containers.InspectContainerAsync(container.ID);

        var sb = new StringBuilder();
        var name = inspect.Name.TrimStart('/');

        sb.AppendLine($"**Container: {name}**");
        sb.AppendLine($"- ID: {inspect.ID[..12]}");
        sb.AppendLine($"- Image: {inspect.Config.Image}");
        sb.AppendLine($"- Status: {inspect.State.Status}");
        sb.AppendLine($"- Running: {inspect.State.Running}");

        if (inspect.State.Running)
        {
            sb.AppendLine($"- Started: {inspect.State.StartedAt}");
            sb.AppendLine($"- PID: {inspect.State.Pid}");
        }
        else if (inspect.State.ExitCode != 0)
        {
            sb.AppendLine($"- Exit Code: {inspect.State.ExitCode}");
            sb.AppendLine($"- Finished: {inspect.State.FinishedAt}");
        }

        if (inspect.State.Health != null)
        {
            sb.AppendLine($"- Health: {inspect.State.Health.Status}");
        }

        // Network info
        if (inspect.NetworkSettings?.Networks?.Any() == true)
        {
            sb.AppendLine("- Networks:");
            foreach (var network in inspect.NetworkSettings.Networks)
            {
                sb.AppendLine($"  - {network.Key}: {network.Value.IPAddress}");
            }
        }

        // Port mappings
        if (inspect.NetworkSettings?.Ports?.Any() == true)
        {
            var boundPorts = inspect.NetworkSettings.Ports
                .Where(p => p.Value?.Any() == true)
                .ToList();

            if (boundPorts.Any())
            {
                sb.AppendLine("- Ports:");
                foreach (var port in boundPorts)
                {
                    foreach (var binding in port.Value)
                    {
                        sb.AppendLine($"  - {binding.HostPort} -> {port.Key}");
                    }
                }
            }
        }

        // Resource usage
        sb.AppendLine($"- Restart Policy: {inspect.HostConfig.RestartPolicy.Name}");

        return sb.ToString();
    }

    [KernelFunction]
    [Description("Starts a stopped container. This is a safe operation.")]
    public virtual async Task<string> StartContainer([Description("Container name or ID")] string containerName)
    {
        _logger.LogInformation("Starting container {Container}...", containerName);

        var container = await FindContainerAsync(containerName);
        if (container == null)
        {
            return $"Container '{containerName}' not found.";
        }

        var name = container.Names.FirstOrDefault()?.TrimStart('/') ?? containerName;

        if (container.State == "running")
        {
            return $"Container '{name}' is already running.";
        }

        await _client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters());

        _logger.LogInformation("Container {Container} started", name);
        return $"Started container '{name}'.";
    }

    [KernelFunction]
    [Description("Stops a running container. DANGEROUS: Ask for confirmation before executing.")]
    public async Task<string> StopContainer([Description("Container name or ID")] string containerName)
    {
        _logger.LogWarning("Stopping container {Container}...", containerName);

        var container = await FindContainerAsync(containerName);
        if (container == null)
        {
            return $"Container '{containerName}' not found.";
        }

        var name = container.Names.FirstOrDefault()?.TrimStart('/') ?? containerName;

        if (container.State != "running")
        {
            return $"Container '{name}' is not running (current state: {container.State}).";
        }

        await _client.Containers.StopContainerAsync(container.ID, new ContainerStopParameters
        {
            WaitBeforeKillSeconds = 10
        });

        _logger.LogInformation("Container {Container} stopped", name);
        return $"Stopped container '{name}'.";
    }

    [KernelFunction]
    [Description("Restarts a container. DANGEROUS: Ask for confirmation before executing.")]
    public virtual async Task<string> RestartContainer([Description("Container name or ID")] string containerName)
    {
        _logger.LogWarning("Restarting container {Container}...", containerName);

        var container = await FindContainerAsync(containerName);
        if (container == null)
        {
            return $"Container '{containerName}' not found.";
        }

        var name = container.Names.FirstOrDefault()?.TrimStart('/') ?? containerName;

        await _client.Containers.RestartContainerAsync(container.ID, new ContainerRestartParameters
        {
            WaitBeforeKillSeconds = 10
        });

        _logger.LogInformation("Container {Container} restarted", name);
        return $"Restarted container '{name}'.";
    }

    [KernelFunction]
    [Description("Gets the recent logs from a Docker container via the Docker API. Returns the last N lines of logs.")]
    public async Task<string> GetContainerLogsFromDocker(
        [Description("Container name or ID")] string containerName,
        [Description("Number of lines to retrieve (default 50)")] int lines = 50)
    {
        _logger.LogDebug("Getting logs for container {Container}...", containerName);

        var container = await FindContainerAsync(containerName);
        if (container == null)
        {
            return $"Container '{containerName}' not found.";
        }

        var name = container.Names.FirstOrDefault()?.TrimStart('/') ?? containerName;

        var logParams = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Tail = lines.ToString(),
            Timestamps = true
        };

        using var stream = await _client.Containers.GetContainerLogsAsync(container.ID, false, logParams);

        // Read from multiplexed stream
        var sb = new StringBuilder();
        var buffer = new byte[4096];

        while (true)
        {
            var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, default);
            if (result.EOF)
            {
                break;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            sb.Append(text);
        }

        var logs = sb.ToString();

        if (string.IsNullOrWhiteSpace(logs))
        {
            return $"No logs available for container '{name}'.";
        }

        var output = new StringBuilder();
        output.AppendLine($"**Logs for {name}** (last {lines} lines):\n```");
        output.Append(logs.Trim());
        output.AppendLine("\n```");

        return output.ToString();
    }

    [KernelFunction]
    [Description("Lists all Docker networks with their connected containers. Useful for understanding service relationships.")]
    public async Task<string> ListNetworks()
    {
        _logger.LogDebug("Listing Docker networks...");

        // Build network → containers mapping from container data (more reliable than network inspect)
        var containers = await _client.Containers.ListContainersAsync(
            new ContainersListParameters { All = true });

        var networkMap = new Dictionary<string, List<string>>();
        foreach (var container in containers)
        {
            var name = container.Names.FirstOrDefault()?.TrimStart('/') ?? container.ID[..12];
            if (container.NetworkSettings?.Networks == null)
            {
                continue;
            }

            foreach (var network in container.NetworkSettings.Networks)
            {
                if (!networkMap.TryGetValue(network.Key, out var members))
                {
                    members = [];
                    networkMap[network.Key] = members;
                }

                members.Add($"{name} ({network.Value.IPAddress})");
            }
        }

        var networks = await _client.Networks.ListNetworksAsync();
        var sb = new StringBuilder();
        sb.AppendLine($"**Docker Networks** ({networks.Count}):\n");

        foreach (var network in networks.OrderBy(n => n.Name))
        {
            var members = networkMap.GetValueOrDefault(network.Name, []);
            sb.AppendLine($"- **{network.Name}** ({network.Driver}) — {members.Count} containers");
            foreach (var member in members)
            {
                sb.AppendLine($"  - {member}");
            }
        }

        return sb.ToString();
    }

    internal async Task<List<ContainerNetworkInfo>> GetContainerNetworkMapAsync()
    {
        var containers = await _client.Containers.ListContainersAsync(
            new ContainersListParameters { All = true });

        var result = new List<ContainerNetworkInfo>();
        foreach (var container in containers)
        {
            var name = container.Names.FirstOrDefault()?.TrimStart('/') ?? container.ID[..12];
            var networks = container.NetworkSettings?.Networks?.Keys.ToList() ?? [];
            var ports = container.Ports?
                .Select(p => $"{p.PrivatePort}/{p.Type}")
                .ToList() ?? [];
            var labels = container.Labels != null
                ? new Dictionary<string, string>(container.Labels)
                : new Dictionary<string, string>();

            result.Add(new ContainerNetworkInfo
            {
                Name = name,
                State = container.State,
                Networks = networks,
                Ports = ports,
                Labels = labels,
                Image = container.Image
            });
        }

        return result;
    }

    private async Task<ContainerListResponse?> FindContainerAsync(string nameOrId)
    {
        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true
        });

        // Try exact match on ID
        var container = containers.FirstOrDefault(c =>
            c.ID.StartsWith(nameOrId, StringComparison.OrdinalIgnoreCase));

        if (container != null)
        {
            return container;
        }

        // Try match on name (Docker adds leading /)
        var searchName = nameOrId.TrimStart('/');
        container = containers.FirstOrDefault(c =>
            c.Names.Any(n => n.TrimStart('/').Equals(searchName, StringComparison.OrdinalIgnoreCase)));

        if (container != null)
        {
            return container;
        }

        // Try partial name match
        container = containers.FirstOrDefault(c =>
            c.Names.Any(n => n.TrimStart('/').Contains(searchName, StringComparison.OrdinalIgnoreCase)));

        return container;
    }
}
