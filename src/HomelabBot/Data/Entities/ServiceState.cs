namespace HomelabBot.Data.Entities;

public sealed class ServiceState
{
    public int Id { get; set; }
    public required string ServiceName { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
