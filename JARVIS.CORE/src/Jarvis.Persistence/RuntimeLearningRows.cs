using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed class RuntimeLearningRow
{
    public string Kind { get; set; } = "";
    public Guid Id { get; set; }
    public string Json { get; set; } = "null";
    public DateTimeOffset? DueAt { get; set; }
}
public sealed class RuntimeLearningControlRow
{
    public int Id { get; set; } = 1;
    public long NextTrainingSequence { get; set; } = 1;
}
internal static class RuntimeLearningModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<RuntimeLearningRow>(b => {
            b.ToTable("RuntimeLearningRows"); b.HasKey(x => new { x.Kind, x.Id });
            b.Property(x => x.Kind).HasMaxLength(32); b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Json).HasColumnType("jsonb"); b.HasIndex(x => new { x.Kind, x.DueAt });
        });
        model.Entity<RuntimeLearningControlRow>(b => {
            b.ToTable("RuntimeLearningState"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
            b.HasData(new RuntimeLearningControlRow());
        });
    }
}
