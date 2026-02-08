using HomelabBot.Configuration;
using HomelabBot.Data;
using HomelabBot.Data.Entities;
using HomelabBot.Models;
using HomelabBot.Services.Voice;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class AlertEscalationService : BackgroundService
{
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly TwilioCallingService _twilioCallingService;
    private readonly IOptionsMonitor<TwilioConfiguration> _config;
    private readonly ILogger<AlertEscalationService> _logger;

    public AlertEscalationService(
        IDbContextFactory<HomelabDbContext> dbFactory,
        TwilioCallingService twilioCallingService,
        IOptionsMonitor<TwilioConfiguration> config,
        ILogger<AlertEscalationService> logger)
    {
        _dbFactory = dbFactory;
        _twilioCallingService = twilioCallingService;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Alert escalation service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_config.CurrentValue.Enabled)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                await ProcessPendingEscalationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing escalations");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    public async Task CreateEscalationAsync(AlertmanagerWebhookAlert alert)
    {
        if (string.IsNullOrEmpty(alert.Fingerprint))
        {
            _logger.LogWarning("Skipping escalation for alert {AlertName}: no fingerprint", alert.AlertName);
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Deduplicate: skip if a pending/in-progress escalation already exists for this fingerprint
        var existing = await db.Set<AlertEscalation>()
            .AnyAsync(e => e.AlertFingerprint == alert.Fingerprint
                && (e.Status == EscalationStatus.Pending || e.Status == EscalationStatus.PhoneCallPlaced));

        if (existing)
        {
            _logger.LogInformation("Escalation already exists for fingerprint {Fingerprint}, skipping", alert.Fingerprint);
            return;
        }

        var escalation = new AlertEscalation
        {
            AlertFingerprint = alert.Fingerprint,
            AlertName = alert.AlertName,
            Severity = alert.Severity,
            Description = alert.Description ?? alert.Summary
        };

        db.Set<AlertEscalation>().Add(escalation);
        await db.SaveChangesAsync();

        _logger.LogInformation("Created escalation {EscalationId} for alert {AlertName}",
            escalation.Id, alert.AlertName);
    }

    public async Task AcknowledgeAsync(int escalationId, string method)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var escalation = await db.Set<AlertEscalation>().FindAsync(escalationId);

        if (escalation == null)
        {
            _logger.LogWarning("Escalation {EscalationId} not found for acknowledgement", escalationId);
            return;
        }

        escalation.Status = EscalationStatus.Acknowledged;
        escalation.AcknowledgedAt = DateTime.UtcNow;
        escalation.AcknowledgementMethod = method;
        await db.SaveChangesAsync();

        _logger.LogInformation("Escalation {EscalationId} acknowledged via {Method}", escalationId, method);
    }

    public async Task AutoResolveAsync(string fingerprint)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var pending = await db.Set<AlertEscalation>()
            .Where(e => e.AlertFingerprint == fingerprint && e.Status == EscalationStatus.Pending)
            .ToListAsync();

        foreach (var escalation in pending)
        {
            escalation.Status = EscalationStatus.AutoResolved;
            _logger.LogInformation("Auto-resolved escalation {EscalationId} for fingerprint {Fingerprint}",
                escalation.Id, fingerprint);
        }

        await db.SaveChangesAsync();
    }

    private async Task ProcessPendingEscalationsAsync(CancellationToken ct)
    {
        var cfg = _config.CurrentValue;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var cutoff = DateTime.UtcNow.AddMinutes(-cfg.EscalationDelayMinutes);
        var pendingEscalations = await db.Set<AlertEscalation>()
            .Where(e => e.Status == EscalationStatus.Pending && e.CreatedAt <= cutoff)
            .ToListAsync(ct);

        foreach (var escalation in pendingEscalations)
        {
            _logger.LogInformation("Processing escalation {EscalationId} for alert {AlertName}",
                escalation.Id, escalation.AlertName);

            var callSid = await _twilioCallingService.InitiateAlertCallAsync(escalation.Id);

            if (callSid != null)
            {
                escalation.Status = EscalationStatus.PhoneCallPlaced;
                escalation.PhoneCallInitiatedAt = DateTime.UtcNow;
                escalation.TwilioCallSid = callSid;
            }
            else
            {
                escalation.Status = EscalationStatus.Failed;
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
