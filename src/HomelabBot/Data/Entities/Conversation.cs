namespace HomelabBot.Data.Entities;

public sealed class Conversation
{
    public int Id { get; set; }
    public ulong ThreadId { get; set; }
    public string? Title { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastMessageAt { get; set; }
    public List<ConversationMessage> Messages { get; set; } = [];
}
