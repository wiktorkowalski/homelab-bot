using System.Security.Cryptography;
using System.Text;
using HomelabBot.Configuration;
using Microsoft.Extensions.Options;

namespace HomelabBot.Mcp;

public sealed class McpApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _apiKey;

    public McpApiKeyMiddleware(RequestDelegate next, IOptions<McpServerConfiguration> config)
    {
        _next = next;
        _apiKey = config.Value.ApiKey;
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
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }
        }

        await _next(context);
    }
}
