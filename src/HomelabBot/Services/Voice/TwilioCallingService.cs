using HomelabBot.Configuration;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace HomelabBot.Services.Voice;

public sealed class TwilioCallingService
{
    private static readonly object InitLock = new();
    private readonly IOptionsMonitor<TwilioConfiguration> _config;
    private readonly ILogger<TwilioCallingService> _logger;
    private volatile bool _initialized;

    public TwilioCallingService(
        IOptionsMonitor<TwilioConfiguration> config,
        ILogger<TwilioCallingService> logger)
    {
        _config = config;
        _logger = logger;
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (InitLock)
        {
            if (_initialized) return;
            var cfg = _config.CurrentValue;
            TwilioClient.Init(cfg.AccountSid, cfg.AuthToken);
            _initialized = true;
        }
    }

    public async Task<string?> InitiateAlertCallAsync(int escalationId)
    {
        var cfg = _config.CurrentValue;
        if (!cfg.Enabled)
        {
            _logger.LogWarning("Twilio is disabled, skipping call for escalation {EscalationId}", escalationId);
            return null;
        }

        try
        {
            EnsureInitialized();

            var twimlUrl = $"{cfg.WebhookBaseUrl.TrimEnd('/')}/api/twilio/alert-twiml?escalationId={escalationId}";
            var statusCallback = $"{cfg.WebhookBaseUrl.TrimEnd('/')}/api/twilio/call-status";

            _logger.LogInformation("Initiating phone call for escalation {EscalationId} to {ToNumber}",
                escalationId, cfg.ToPhoneNumber);

            var call = await CallResource.CreateAsync(
                to: new PhoneNumber(cfg.ToPhoneNumber),
                from: new PhoneNumber(cfg.FromPhoneNumber),
                url: new Uri(twimlUrl),
                statusCallback: new Uri(statusCallback));

            _logger.LogInformation("Call initiated: SID={CallSid} for escalation {EscalationId}",
                call.Sid, escalationId);

            return call.Sid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initiate call for escalation {EscalationId}", escalationId);
            return null;
        }
    }
}
