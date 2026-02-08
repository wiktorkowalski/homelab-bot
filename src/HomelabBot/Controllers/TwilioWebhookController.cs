using HomelabBot.Data;
using HomelabBot.Data.Entities;
using HomelabBot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomelabBot.Controllers;

[ApiController]
[Route("api/twilio")]
public sealed class TwilioWebhookController : ControllerBase
{
    private readonly AlertEscalationService _escalationService;
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly ILogger<TwilioWebhookController> _logger;

    public TwilioWebhookController(
        AlertEscalationService escalationService,
        IDbContextFactory<HomelabDbContext> dbFactory,
        ILogger<TwilioWebhookController> logger)
    {
        _escalationService = escalationService;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    [HttpGet("alert-twiml")]
    public async Task<IActionResult> GetAlertTwiml([FromQuery] int escalationId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var escalation = await db.Set<AlertEscalation>().FindAsync(escalationId);

        if (escalation == null)
        {
            var notFoundXml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <Response>
                    <Say voice="alice">Sorry, escalation not found.</Say>
                    <Hangup/>
                </Response>
                """;
            return Content(notFoundXml, "application/xml");
        }

        var gatherUrl = $"{Request.Scheme}://{Request.Host}/api/twilio/gather-response?escalationId={escalationId}";

        var twiml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Response>
                <Say voice="alice">Homelab alert escalation. {System.Security.SecurityElement.Escape(escalation.AlertName)}. Severity: {System.Security.SecurityElement.Escape(escalation.Severity)}. {System.Security.SecurityElement.Escape(escalation.Description ?? "No description available")}.</Say>
                <Gather numDigits="1" action="{gatherUrl}" method="POST">
                    <Say voice="alice">Press 1 to acknowledge this alert. Press any other key to ignore.</Say>
                </Gather>
                <Say voice="alice">No input received. Goodbye.</Say>
                <Hangup/>
            </Response>
            """;

        return Content(twiml, "application/xml");
    }

    [HttpPost("gather-response")]
    public async Task<IActionResult> HandleGatherResponse([FromQuery] int escalationId, [FromForm] string digits)
    {
        _logger.LogInformation("DTMF response for escalation {EscalationId}: {Digits}", escalationId, digits);

        string responseXml;
        if (digits == "1")
        {
            await _escalationService.AcknowledgeAsync(escalationId, "phone-dtmf");
            responseXml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <Response>
                    <Say voice="alice">Alert acknowledged. Thank you.</Say>
                    <Hangup/>
                </Response>
                """;
        }
        else
        {
            responseXml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <Response>
                    <Say voice="alice">Alert not acknowledged. Goodbye.</Say>
                    <Hangup/>
                </Response>
                """;
        }

        return Content(responseXml, "application/xml");
    }

    [HttpPost("call-status")]
    public IActionResult HandleCallStatus([FromForm] string callSid, [FromForm] string callStatus)
    {
        _logger.LogInformation("Call status update: SID={CallSid}, Status={Status}", callSid, callStatus);
        return Ok();
    }
}
