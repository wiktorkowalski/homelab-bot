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

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();

    public DbSet<LlmInteraction> LlmInteractions => Set<LlmInteraction>();

    public DbSet<ToolCallLog> ToolCallLogs => Set<ToolCallLog>();

    public DbSet<HealthScoreHistory> HealthScoreHistory => Set<HealthScoreHistory>();

    public DbSet<Runbook> Runbooks => Set<Runbook>();

    public DbSet<AnomalyEvent> AnomalyEvents => Set<AnomalyEvent>();

    public DbSet<ContainerCriticality> ContainerCriticalities => Set<ContainerCriticality>();

    public DbSet<RemediationAction> RemediationActions => Set<RemediationAction>();

    public DbSet<HealingChain> HealingChains => Set<HealingChain>();

    public DbSet<WarRoom> WarRooms => Set<WarRoom>();

    public DbSet<ServiceState> ServiceStates => Set<ServiceState>();

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

        modelBuilder.Entity<HealthScoreHistory>(entity =>
        {
            entity.HasIndex(h => h.RecordedAt);
        });

        modelBuilder.Entity<Runbook>(entity =>
        {
            entity.HasIndex(r => r.Enabled);
            entity.HasIndex(r => r.TriggerCondition);
        });

        modelBuilder.Entity<AnomalyEvent>(entity =>
        {
            entity.HasIndex(a => a.DetectedAt);
            entity.HasOne(a => a.Investigation)
                .WithMany()
                .HasForeignKey(a => a.InvestigationId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ContainerCriticality>(entity =>
        {
            entity.HasIndex(c => c.ContainerName).IsUnique();
        });

        modelBuilder.Entity<RemediationAction>(entity =>
        {
            entity.HasIndex(a => a.ExecutedAt);
        });

        modelBuilder.Entity<HealingChain>(entity =>
        {
            entity.HasIndex(h => h.Status);
        });

        modelBuilder.Entity<WarRoom>(entity =>
        {
            entity.HasIndex(w => w.Status);
            entity.HasIndex(w => w.DiscordThreadId);
            entity.HasOne(w => w.Investigation)
                .WithMany()
                .HasForeignKey(w => w.InvestigationId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ServiceState>(entity =>
        {
            entity.HasIndex(s => new { s.ServiceName, s.Key }).IsUnique();
        });
    }
}
