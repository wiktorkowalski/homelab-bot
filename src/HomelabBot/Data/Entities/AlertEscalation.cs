namespace HomelabBot.Data.Entities;

public enum EscalationStatus
{
    Pending,
    PhoneCallPlaced,
    Acknowledged,
    AutoResolved,
    Failed
}

public sealed class AlertEscalation
{
    public int Id { get; set; }
    public required string AlertFingerprint { get; set; }
    public required string AlertName { get; set; }
    public required string Severity { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public EscalationStatus Status { get; set; } = EscalationStatus.Pending;
    public DateTime? PhoneCallInitiatedAt { get; set; }
    public string? TwilioCallSid { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgementMethod { get; set; }
}
