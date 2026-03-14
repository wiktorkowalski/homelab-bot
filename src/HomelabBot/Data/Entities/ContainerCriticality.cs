namespace HomelabBot.Data.Entities;

public sealed class ContainerCriticality
{
    public int Id { get; set; }
    public required string ContainerName { get; set; }
    public bool IsCritical { get; set; }
    public string? Notes { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
