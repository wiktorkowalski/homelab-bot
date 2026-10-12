using HomelabBot.Mcp;
using HomelabBot.Plugins;

namespace HomelabBot.Extensions;

internal static class McpServerBuilderExtensions
{
    // The whole tool surface of the MCP server. Tests call this too, so they run the wiring of Program.cs.
    public static IMcpServerBuilder WithHomelabTools(this IMcpServerBuilder builder) =>
        builder
            .WithTools<DockerPlugin>()
            .WithTools<PrometheusPlugin>()
            .WithTools<AlertmanagerPlugin>()
            .WithTools<LokiPlugin>()
            .WithTools<GrafanaPlugin>()
            .WithTools<MikroTikPlugin>()
            .WithTools<TrueNASPlugin>()
            .WithTools<HomeAssistantPlugin>()
            .WithTools<NtfyPlugin>()
            .WithTools<KnowledgePlugin>()
            .WithTools<InvestigationPlugin>()
            .WithTools<RunbookPlugin>()
            .WithTools<HomelabMcpTools>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(ToolArgumentErrorFilter.Wrap));
}
