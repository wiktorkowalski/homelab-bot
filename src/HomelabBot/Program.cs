using HomelabBot.Configuration;
using HomelabBot.Plugins;
using HomelabBot.Services;
using Serilog;

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

    // HTTP clients with resilience
    builder.Services.AddHttpClient("Default")
        .AddStandardResilienceHandler();

    // Plugins
    builder.Services.AddSingleton<DockerPlugin>();
    builder.Services.AddSingleton<PrometheusPlugin>();
    builder.Services.AddSingleton<AlertmanagerPlugin>();
    builder.Services.AddSingleton<LokiPlugin>();
    builder.Services.AddSingleton<GrafanaPlugin>();
    builder.Services.AddSingleton<MikroTikPlugin>();
    builder.Services.AddSingleton<TrueNASPlugin>();
    builder.Services.AddSingleton<HomeAssistantPlugin>();
    builder.Services.AddSingleton<NtfyPlugin>();

    // Services
    builder.Services.AddSingleton<UrlService>();
    builder.Services.AddSingleton<ConversationService>();
    builder.Services.AddSingleton<ConfirmationService>();
    builder.Services.AddSingleton<KernelService>();
    builder.Services.AddHostedService<DiscordBotService>();

    // Health checks
    builder.Services.AddHealthChecks();

    var app = builder.Build();

    app.MapHealthChecks("/health");

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
