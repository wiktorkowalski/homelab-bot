using HomelabBot.Configuration;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class UrlService
{
    private readonly Dictionary<string, string> _externalUrls;

    public UrlService(IOptions<BotConfiguration> config)
    {
        _externalUrls = config.Value.ExternalUrls;
    }

    public string GetExternalUrl(string serviceName, string internalUrl)
    {
        if (_externalUrls.TryGetValue(serviceName.ToLowerInvariant(), out var externalUrl))
        {
            return externalUrl.TrimEnd('/');
        }

        return internalUrl;
    }

    public string GetExternalUrl(string serviceName, string internalUrl, string path)
    {
        var baseUrl = GetExternalUrl(serviceName, internalUrl);
        return $"{baseUrl}/{path.TrimStart('/')}";
    }

    public string BuildRedirectUrl(string targetUrl, string returnUrl)
    {
        var encodedReturnUrl = Uri.EscapeDataString(returnUrl ?? string.Empty);
        return $"{targetUrl}?returnUrl={encodedReturnUrl}";
    }
}
