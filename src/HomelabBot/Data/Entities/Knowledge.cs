namespace HomelabBot.Data.Entities;

public sealed class Knowledge
{
    public int Id { get; set; }
    public required string Topic { get; set; }
    public required string Fact { get; set; }
    public string? Context { get; set; }
    public double Confidence { get; set; } = 0.8;
    public string Source { get; set; } = "discovered";
    public bool IsValid { get; set; } = true;
    public DateTime? LastVerified { get; set; }
    public int? ContradictsId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsed { get; set; }
}
