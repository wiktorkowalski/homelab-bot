using HomelabBot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HomelabBot.Data;

public sealed class HomelabDbContext : DbContext
{
    public HomelabDbContext(DbContextOptions<HomelabDbContext> options)
        : base(options)
    {
    }

    public DbSet<Knowledge> Knowledge => Set<Knowledge>();

    public DbSet<Investigation> Investigations => Set<Investigation>();

    public DbSet<InvestigationStep> InvestigationSteps => Set<InvestigationStep>();

    public DbSet<Pattern> Patterns => Set<Pattern>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();

    public DbSet<LlmInteraction> LlmInteractions => Set<LlmInteraction>();

    public DbSet<ToolCallLog> ToolCallLogs => Set<ToolCallLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Knowledge>(entity =>
        {
            entity.HasIndex(k => k.Topic);
            entity.HasIndex(k => new { k.Topic, k.IsValid });
        });

        modelBuilder.Entity<Investigation>(entity =>
        {
            entity.HasIndex(i => i.ThreadId);
            entity.HasIndex(i => i.Resolved);
        });

        modelBuilder.Entity<InvestigationStep>(entity =>
        {
            entity.HasOne(s => s.Investigation)
                .WithMany(i => i.Steps)
                .HasForeignKey(s => s.InvestigationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Pattern>(entity =>
        {
            entity.HasIndex(p => p.Symptom);
        });

        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.HasIndex(c => c.ThreadId).IsUnique();
        });

        modelBuilder.Entity<ConversationMessage>(entity =>
        {
            entity.HasOne(m => m.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LlmInteraction>(entity =>
        {
            entity.HasIndex(i => i.ThreadId);
            entity.HasIndex(i => i.Timestamp);
            entity.HasOne(i => i.Conversation)
                .WithMany()
                .HasForeignKey(i => i.ConversationId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ToolCallLog>(entity =>
        {
            entity.HasOne(t => t.LlmInteraction)
                .WithMany(i => i.ToolCalls)
                .HasForeignKey(t => t.LlmInteractionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
