using System.Text;
using DotNetEnv;
using HomelabBot.Configuration;
using HomelabBot.Data;
using HomelabBot.Mcp;
using HomelabBot.Plugins;
using HomelabBot.Services;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

// Load .env file if present (for local development)
Env.TraversePath().Load();

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting HomeLabBot...");

    var builder = WebApplication.CreateBuilder(args);

    // Serilog
    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services));

    // Configuration
    builder.Services.AddOptions<BotConfiguration>()
        .Bind(builder.Configuration.GetSection(BotConfiguration.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddOptions<MikroTikConfiguration>()
        .Bind(builder.Configuration.GetSection(MikroTikConfiguration.SectionName));

    builder.Services.AddOptions<TrueNASConfiguration>()
        .Bind(builder.Configuration.GetSection(TrueNASConfiguration.SectionName));

    builder.Services.AddOptions<HomeAssistantConfiguration>()
        .Bind(builder.Configuration.GetSection(HomeAssistantConfiguration.SectionName));

    builder.Services.AddOptions<PrometheusConfiguration>()
        .Bind(builder.Configuration.GetSection(PrometheusConfiguration.SectionName));

    builder.Services.AddOptions<GrafanaConfiguration>()
        .Bind(builder.Configuration.GetSection(GrafanaConfiguration.SectionName));

    builder.Services.AddOptions<AlertmanagerConfiguration>()
        .Bind(builder.Configuration.GetSection(AlertmanagerConfiguration.SectionName));

    builder.Services.AddOptions<LokiConfiguration>()
        .Bind(builder.Configuration.GetSection(LokiConfiguration.SectionName));

    builder.Services.AddOptions<NtfyConfiguration>()
        .Bind(builder.Configuration.GetSection(NtfyConfiguration.SectionName));

    builder.Services.AddOptions<LangfuseConfiguration>()
        .Bind(builder.Configuration.GetSection(LangfuseConfiguration.SectionName));

    builder.Services.AddOptions<DailySummaryConfiguration>()
        .Bind(builder.Configuration.GetSection(DailySummaryConfiguration.SectionName));

    builder.Services.AddOptions<AlertWebhookConfiguration>()
        .Bind(builder.Configuration.GetSection(AlertWebhookConfiguration.SectionName));

    builder.Services.AddOptions<HealthScoreConfiguration>()
        .Bind(builder.Configuration.GetSection(HealthScoreConfiguration.SectionName));

    builder.Services.AddOptions<AnomalyDetectionConfiguration>()
        .Bind(builder.Configuration.GetSection(AnomalyDetectionConfiguration.SectionName));

    builder.Services.AddOptions<SecurityAuditConfiguration>()
        .Bind(builder.Configuration.GetSection(SecurityAuditConfiguration.SectionName));

    builder.Services.AddOptions<LogAnomalyConfiguration>()
        .Bind(builder.Configuration.GetSection(LogAnomalyConfiguration.SectionName));

    builder.Services.AddOptions<KnowledgeRefreshConfiguration>()
        .Bind(builder.Configuration.GetSection(KnowledgeRefreshConfiguration.SectionName));

    builder.Services.AddOptions<AutoRemediationConfiguration>()
        .Bind(builder.Configuration.GetSection(AutoRemediationConfiguration.SectionName));

    builder.Services.AddOptions<WarRoomConfiguration>()
        .Bind(builder.Configuration.GetSection(WarRoomConfiguration.SectionName));

    builder.Services.AddOptions<HealingChainConfiguration>()
        .Bind(builder.Configuration.GetSection(HealingChainConfiguration.SectionName));

    builder.Services.AddOptions<McpServerConfiguration>()
        .Bind(builder.Configuration.GetSection(McpServerConfiguration.SectionName));

    // MCP Server
    var mcpConfig = builder.Configuration.GetSection(McpServerConfiguration.SectionName).Get<McpServerConfiguration>();
    if (mcpConfig is { Enabled: true, ApiKey.Length: > 0 })
    {
        builder.Services.AddMcpServer()
            .WithHttpTransport()
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
            .WithTools<HomelabMcpTools>();
    }

    // Langfuse/OpenTelemetry
    var langfuseConfig = builder.Configuration.GetSection(LangfuseConfiguration.SectionName).Get<LangfuseConfiguration>();
    if (langfuseConfig is not null)
    {
        Log.Information("Langfuse telemetry endpoint: {Endpoint}", langfuseConfig.Endpoint);

        // Enable Semantic Kernel diagnostics (both required for full tracing)
        AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnostics", true);
        AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnosticsSensitive", true);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("homelab-bot"))
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new AlwaysOnSampler())
                    .AddSource("HomelabBot.Chat")
                    .AddSource("Microsoft.SemanticKernel*")
                    .AddProcessor(new LangfuseEnrichmentProcessor())
                    .AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(langfuseConfig.Endpoint);
                        options.Protocol = OtlpExportProtocol.HttpProtobuf;
                        options.Headers = $"Authorization=Basic {Convert.ToBase64String(
                            Encoding.UTF8.GetBytes($"{langfuseConfig.PublicKey}:{langfuseConfig.SecretKey}"))}";
                    });
            });
    }

    // Database
    var dataPath = Environment.GetEnvironmentVariable("DATA_PATH") ?? "data";
    var dbPath = Path.Combine(dataPath, "homelab.db");
    Directory.CreateDirectory(dataPath);

    builder.Services.AddDbContextFactory<HomelabDbContext>(options =>
        options.UseSqlite($"Data Source={dbPath}"));

    // HTTP clients with resilience
    builder.Services.AddHttpClient("Default")
        .AddStandardResilienceHandler();

    // Shared services
    builder.Services.AddSingleton<PrometheusQueryService>();

    // Plugins
    builder.Services.AddSingleton<Docker.DotNet.DockerClient>(_ =>
        new Docker.DotNet.DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient());
    builder.Services.AddSingleton<DockerPlugin>();
    builder.Services.AddSingleton<PrometheusPlugin>();
    builder.Services.AddSingleton<AlertmanagerPlugin>();
    builder.Services.AddSingleton<LokiPlugin>();
    builder.Services.AddSingleton<GrafanaPlugin>();
    builder.Services.AddSingleton<MikroTikPlugin>();
    builder.Services.AddSingleton<TrueNASPlugin>();
    builder.Services.AddSingleton<HomeAssistantPlugin>();
    builder.Services.AddSingleton<NtfyPlugin>();
    builder.Services.AddSingleton<KnowledgePlugin>();
    builder.Services.AddSingleton<InvestigationPlugin>();
    builder.Services.AddSingleton<RunbookPlugin>();

    // Services
    builder.Services.AddSingleton<IncidentSimilarityService>();
    builder.Services.AddSingleton<RunbookCompilerService>();
    builder.Services.AddSingleton<KnowledgeService>();
    builder.Services.AddSingleton<MemoryService>();
    builder.Services.AddSingleton<UrlService>();
    builder.Services.AddSingleton<ConversationService>();
    builder.Services.AddSingleton<ConfirmationService>();
    builder.Services.AddSingleton<TelemetryService>();
    builder.Services.AddSingleton<KernelService>();
    builder.Services.AddSingleton<DiscordBotService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<DiscordBotService>());
    builder.Services.AddSingleton<HealthScoreService>();
    builder.Services.AddSingleton<ServiceStateStore>();
    builder.Services.AddSingleton<SummaryDataAggregator>();
    builder.Services.AddSingleton<SmartNotificationService>();
    builder.Services.AddHostedService<DailySummaryService>();
    builder.Services.AddSingleton<AutoRemediationService>();
    builder.Services.AddSingleton<HealingChainService>();
    builder.Services.AddSingleton<ContagionTrackerService>();
    builder.Services.AddSingleton<WarRoomService>();
    builder.Services.AddSingleton<RunbookTriggerService>();
    builder.Services.AddSingleton<AlertWebhookService>();
    builder.Services.AddHostedService<HealthScoreBackgroundService>();
    builder.Services.AddSingleton<SecurityAuditService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SecurityAuditService>());
    builder.Services.AddHostedService<AnomalyDetectionService>();
    builder.Services.AddHostedService<LogAnomalyService>();
    builder.Services.AddHostedService<KnowledgeRefreshService>();

    // API Controllers
    builder.Services.AddControllers();

    // Health checks
    builder.Services.AddHealthChecks();

    var app = builder.Build();

    // Apply database migrations
    var dbFactory = app.Services.GetRequiredService<IDbContextFactory<HomelabDbContext>>();
    await using (var db = await dbFactory.CreateDbContextAsync())
    {
        await db.Database.MigrateAsync();
    }

    // Seed default runbooks
    await app.Services.GetRequiredService<RunbookTriggerService>().SeedDefaultRunbooksAsync();

    // Static files for Admin Dashboard
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // MCP Server endpoint
    if (mcpConfig is { Enabled: true, ApiKey.Length: > 0 })
    {
        app.UseMiddleware<McpApiKeyMiddleware>();
        app.MapMcp("/mcp");
    }

    // API endpoints
    app.MapControllers();
    app.MapHealthChecks("/health");

    // SPA fallback
    app.MapFallbackToFile("index.html");

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;
