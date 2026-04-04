using System.Security.Cryptography;
using System.Text;
using HomelabBot.Configuration;
using Microsoft.Extensions.Options;

namespace HomelabBot.Mcp;

public sealed class McpApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _apiKey;
    private readonly ILogger<McpApiKeyMiddleware> _logger;

    public McpApiKeyMiddleware(RequestDelegate next, IOptions<McpServerConfiguration> config, ILogger<McpApiKeyMiddleware> logger)
    {
        _next = next;
        _apiKey = config.Value.ApiKey;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/mcp"))
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authHeader["Bearer ".Length..]
                : authHeader;

            var tokenBytes = Encoding.UTF8.GetBytes(token);
            var apiKeyBytes = Encoding.UTF8.GetBytes(_apiKey);
            if (string.IsNullOrEmpty(token)
                || tokenBytes.Length != apiKeyBytes.Length
                || !CryptographicOperations.FixedTimeEquals(tokenBytes, apiKeyBytes))
            {
                _logger.LogWarning("MCP auth failed for {Path} from {RemoteIp}", context.Request.Path, context.Connection.RemoteIpAddress);
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }

            _logger.LogInformation("MCP request authenticated for {Path}", context.Request.Path);
        }

        await _next(context);
    }
}
