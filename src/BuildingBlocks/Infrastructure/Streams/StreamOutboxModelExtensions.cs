using Microsoft.EntityFrameworkCore;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public static class StreamOutboxModelExtensions
{
    public static ModelBuilder ConfigureStreamOutbox(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StreamOutboxMessage>(entity =>
        {
            entity.ToTable("StreamOutboxMessages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Topic).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Key).HasMaxLength(500).IsRequired();
            entity.Property(x => x.EventType).HasMaxLength(200).IsRequired();
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.HeadersJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.LastError).HasMaxLength(2_000);
            entity.HasIndex(x => new { x.PublishedAt, x.AvailableAt })
                .HasFilter("\"PublishedAt\" IS NULL");
            entity.HasIndex(x => new { x.Topic, x.Key });
            entity.HasIndex(x => new { x.TenantId, x.OccurredAt });
        });

        return modelBuilder;
    }
}
