using HomelabBot.Models;
using HomelabBot.Services;
using Microsoft.AspNetCore.Mvc;

namespace HomelabBot.Controllers;

[ApiController]
[Route("api/webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly AlertWebhookService _alertService;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(AlertWebhookService alertService, ILogger<WebhooksController> logger)
    {
        _alertService = alertService;
        _logger = logger;
    }

    [HttpPost("alertmanager")]
    public async Task<IActionResult> AlertmanagerWebhook(
        [FromBody] AlertmanagerWebhookPayload payload,
        CancellationToken ct)
    {
        _logger.LogInformation("Received Alertmanager webhook: {Status}, {AlertCount} alerts",
            payload.Status, payload.Alerts.Count);

        await _alertService.ProcessAlertsAsync(payload, ct);

        return Ok();
    }
}
