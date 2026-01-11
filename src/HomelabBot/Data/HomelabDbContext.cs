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
    }
}
