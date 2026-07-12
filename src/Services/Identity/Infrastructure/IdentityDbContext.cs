using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Domain.Authz;
using ThreeCommerce.Identity.Domain.Tenancy;

namespace ThreeCommerce.Identity.Infrastructure;

public class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<EmailToken> EmailTokens => Set<EmailToken>();
    public DbSet<MfaEnrollment> MfaEnrollments => Set<MfaEnrollment>();

    // Multi-tenant foundation (ADR-0023/0025/0026).
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Principal> Principals => Set<Principal>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<ServiceAccount> ServiceAccounts => Set<ServiceAccount>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<MembershipRole> MembershipRoles => Set<MembershipRole>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("identity");

        modelBuilder.Entity<User>(user =>
        {
            // citext: case-insensitive uniqueness at the database level.
            user.Property(u => u.Email).HasColumnType("citext");
            user.Property(u => u.Title).HasMaxLength(20);
            user.Property(u => u.FirstName).HasMaxLength(100);
            user.Property(u => u.MiddleName).HasMaxLength(100);
            user.Property(u => u.LastName).HasMaxLength(100);
            user.Property(u => u.PreferredName).HasMaxLength(100);
            user.Property(u => u.Phone).HasMaxLength(32);
            user.Ignore(u => u.DisplayName);
            user.Ignore(u => u.FullName);
            user.HasIndex(u => new { u.TenantId, u.Email }).IsUnique();
            user.HasIndex(u => u.PrincipalId).IsUnique();
            user.HasMany(u => u.Addresses).WithOne().HasForeignKey(a => a.UserId);
            user.HasOne<Principal>().WithMany().HasForeignKey(u => u.PrincipalId);
            user.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId);
        });

        modelBuilder.Entity<Address>(address =>
        {
            address.Property(a => a.Purpose).HasConversion<string>().HasMaxLength(16);
            address.HasIndex(a => new { a.TenantId, a.UserId });
            address.HasIndex(a => new { a.UserId, a.Purpose, a.IsDefault });
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

        modelBuilder.Entity<MfaEnrollment>(mfa =>
        {
            // One factor per user; keyed by UserId (no TenantId — isolation is transitive via Users).
            mfa.HasIndex(m => m.UserId).IsUnique();
            mfa.HasOne<User>().WithMany().HasForeignKey(m => m.UserId);
        });

        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.HasIndex(t => t.Slug).IsUnique();
        });

        modelBuilder.Entity<Principal>();

        modelBuilder.Entity<TenantMembership>(m =>
        {
            // One membership per principal per tenant.
            m.HasIndex(x => new { x.TenantId, x.PrincipalId }).IsUnique();
            m.HasIndex(x => x.PrincipalId);
            m.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId);
            m.HasOne<Principal>().WithMany().HasForeignKey(x => x.PrincipalId);
        });

        modelBuilder.Entity<ServiceAccount>(sa =>
        {
            sa.HasIndex(x => x.ClientId).IsUnique();
            sa.HasOne<Principal>().WithMany().HasForeignKey(x => x.PrincipalId);
        });

        // Permission is the persisted snapshot of the code-defined registry (ADR-0025).
        modelBuilder.Entity<Permission>(p => p.HasKey(x => x.Key));

        modelBuilder.Entity<Role>(role =>
        {
            // Key unique within a tenant (null tenant = system/template roles).
            role.HasIndex(r => new { r.TenantId, r.Key }).IsUnique();
            role.HasMany(r => r.Permissions).WithOne().HasForeignKey(rp => rp.RoleId);
        });

        modelBuilder.Entity<RolePermission>(rp =>
        {
            rp.HasKey(x => new { x.RoleId, x.PermissionKey });
            rp.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionKey);
        });

        modelBuilder.Entity<MembershipRole>(mr =>
        {
            mr.HasKey(x => new { x.TenantMembershipId, x.RoleId });
            mr.HasOne<TenantMembership>().WithMany().HasForeignKey(x => x.TenantMembershipId);
            mr.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId);
        });

        // MassTransit transactional outbox + inbox tables (ADR-0007).
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
