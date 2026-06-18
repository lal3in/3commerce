using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.Identity.Infrastructure;

public class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<EmailToken> EmailTokens => Set<EmailToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("identity");

        modelBuilder.Entity<User>(user =>
        {
            // citext: case-insensitive uniqueness at the database level.
            user.Property(u => u.Email).HasColumnType("citext");
            user.HasIndex(u => u.Email).IsUnique();
            user.HasMany(u => u.Addresses).WithOne().HasForeignKey(a => a.UserId);
        });

        modelBuilder.Entity<Session>(session =>
        {
            session.HasIndex(s => s.TokenHash).IsUnique();
            session.HasIndex(s => s.UserId);
        });

        modelBuilder.Entity<EmailToken>(token =>
        {
            token.HasIndex(t => t.TokenHash).IsUnique();
        });

        // MassTransit transactional outbox + inbox tables (ADR-0007).
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
