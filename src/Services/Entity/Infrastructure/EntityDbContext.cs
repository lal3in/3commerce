using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.Entity.Domain;

namespace ThreeCommerce.Entity.Infrastructure;

public sealed class EntityDbContext(DbContextOptions<EntityDbContext> options) : DbContext(options)
{
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<EntityRecord> Entities => Set<EntityRecord>();
    public DbSet<EntityProfile> EntityProfiles => Set<EntityProfile>();
    public DbSet<EntityAddress> EntityAddresses => Set<EntityAddress>();
    public DbSet<EntityIdentifier> EntityIdentifiers => Set<EntityIdentifier>();
    public DbSet<EntityContactMethod> EntityContactMethods => Set<EntityContactMethod>();
    public DbSet<EntityRelationship> EntityRelationships => Set<EntityRelationship>();
    public DbSet<DuplicateWarning> DuplicateWarnings => Set<DuplicateWarning>();
    public DbSet<SupplierOnboarding> SupplierOnboardings => Set<SupplierOnboarding>();
    public DbSet<SupplierChangeRequest> SupplierChangeRequests => Set<SupplierChangeRequest>();
    public DbSet<CustomerEntityLink> CustomerEntityLinks => Set<CustomerEntityLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("entity");
        modelBuilder.ConfigureAudit();

        modelBuilder.Entity<EntityRecord>(entity =>
        {
            entity.ToTable("Entities");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.LegalName).HasMaxLength(200);
            entity.Property(e => e.TradingName).HasMaxLength(200);
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.HasIndex(e => new { e.TenantId, e.DisplayName });
            entity.HasIndex(e => new { e.TenantId, e.Type });
            entity.HasIndex(e => new { e.TenantId, e.Status });
            entity.HasMany(e => e.Profiles).WithOne().HasForeignKey(p => p.EntityId);
            entity.HasMany(e => e.Addresses).WithOne().HasForeignKey(a => a.EntityId);
            entity.HasMany(e => e.Identifiers).WithOne().HasForeignKey(i => i.EntityId);
            entity.HasMany(e => e.ContactMethods).WithOne().HasForeignKey(c => c.EntityId);
        });

        modelBuilder.Entity<EntityProfile>(profile =>
        {
            profile.ToTable("EntityProfiles");
            profile.HasKey(p => p.Id);
            profile.HasIndex(p => new { p.EntityId, p.Role }).IsUnique();
            profile.HasIndex(p => new { p.Role, p.Status });
        });

        modelBuilder.Entity<EntityAddress>(address =>
        {
            address.ToTable("EntityAddresses");
            address.HasKey(a => a.Id);
            address.Property(a => a.Line1).HasMaxLength(200);
            address.Property(a => a.Line2).HasMaxLength(200);
            address.Property(a => a.City).HasMaxLength(120);
            address.Property(a => a.Region).HasMaxLength(120);
            address.Property(a => a.Postcode).HasMaxLength(32);
            address.Property(a => a.CountryCode).HasMaxLength(2);
            address.HasIndex(a => new { a.EntityId, a.Purpose, a.Version }).IsUnique();
            address.HasIndex(a => new { a.EntityId, a.Purpose, a.IsCurrent });
        });

        modelBuilder.Entity<EntityIdentifier>(identifier =>
        {
            identifier.ToTable("EntityIdentifiers");
            identifier.HasKey(i => i.Id);
            identifier.Property(i => i.Value).HasMaxLength(80);
            identifier.HasIndex(i => new { i.Type, i.Value });
            identifier.HasIndex(i => new { i.EntityId, i.Type });
        });

        modelBuilder.Entity<EntityContactMethod>(contact =>
        {
            contact.ToTable("EntityContactMethods");
            contact.HasKey(c => c.Id);
            contact.Property(c => c.Value).HasMaxLength(320);
            contact.HasIndex(c => new { c.EntityId, c.Purpose, c.Kind });
            contact.HasIndex(c => new { c.Kind, c.Value });
        });

        modelBuilder.Entity<EntityRelationship>(relationship =>
        {
            relationship.ToTable("EntityRelationships");
            relationship.HasKey(r => r.Id);
            relationship.HasIndex(r => new { r.TenantId, r.FromEntityId, r.Type, r.EffectiveTo });
            relationship.HasIndex(r => new { r.TenantId, r.ToEntityId, r.Type, r.EffectiveTo });
        });

        modelBuilder.Entity<DuplicateWarning>(warning =>
        {
            warning.ToTable("DuplicateWarnings");
            warning.HasKey(w => w.Id);
            warning.Property(w => w.MatchedValue).HasMaxLength(320);
            warning.Property(w => w.OverrideReason).HasMaxLength(500);
            warning.HasIndex(w => new { w.TenantId, w.CandidateEntityId, w.Status });
            warning.HasIndex(w => new { w.TenantId, w.Kind, w.MatchedValue });
        });

        modelBuilder.Entity<SupplierOnboarding>(supplier =>
        {
            supplier.ToTable("SupplierOnboardings");
            supplier.HasKey(s => s.Id);
            supplier.Property(s => s.SuspensionReason).HasMaxLength(500);
            supplier.HasIndex(s => new { s.TenantId, s.EntityId }).IsUnique();
            supplier.HasIndex(s => new { s.TenantId, s.State });
        });

        modelBuilder.Entity<SupplierChangeRequest>(request =>
        {
            request.ToTable("SupplierChangeRequests");
            request.HasKey(r => r.Id);
            request.Property(r => r.Summary).HasMaxLength(300);
            request.Property(r => r.Detail).HasMaxLength(2000);
            request.Property(r => r.DecisionReason).HasMaxLength(500);
            request.HasIndex(r => new { r.TenantId, r.Status });
            request.HasIndex(r => new { r.TenantId, r.EntityId });
        });

        modelBuilder.Entity<CustomerEntityLink>(link =>
        {
            link.ToTable("CustomerEntityLinks");
            link.HasKey(l => l.Id);
            link.HasIndex(l => new { l.TenantId, l.EntityId });
            link.HasIndex(l => new { l.TenantId, l.CustomerPrincipalId });
            // At most one active link of a given role per (customer, entity).
            link.HasIndex(l => new { l.TenantId, l.CustomerPrincipalId, l.EntityId, l.Role })
                .IsUnique()
                .HasFilter("\"EffectiveTo\" IS NULL");
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
