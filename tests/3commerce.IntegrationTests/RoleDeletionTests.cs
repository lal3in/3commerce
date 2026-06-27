using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using ThreeCommerce.Identity.Domain.Authz;
using ThreeCommerce.Identity.Domain.Tenancy;
using ThreeCommerce.Identity.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Role deletion (aui_6): RbacManagementService.DeleteRoleAsync deletes a custom role but refuses a
/// built-in role and a role still assigned to a member (so deleting never silently de-permissions users).
/// </summary>
[Trait("Category", "Integration")]
public class RoleDeletionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private static readonly Guid Tenant = Guid.NewGuid();
    private Guid _customId;
    private Guid _builtInId;
    private Guid _inUseId;
    private string _cs = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        await using (var admin = new NpgsqlConnection(_cs))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS citext;";
            await cmd.ExecuteNonQueryAsync();
        }

        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();

        var now = DateTimeOffset.UtcNow;
        db.Tenants.Add(new Tenant { Id = Tenant, Name = "T", Slug = $"t-{Tenant:N}", HomeRegion = "dev", Status = TenantStatus.Active, CreatedAt = now });
        var custom = new Role { Id = Guid.CreateVersion7(), TenantId = Tenant, Key = "custom", Name = "Custom", CreatedAt = now };
        var builtIn = new Role { Id = Guid.CreateVersion7(), TenantId = Tenant, Key = "builtin", Name = "Built-in", IsBuiltIn = true, CreatedAt = now };
        var inUse = new Role { Id = Guid.CreateVersion7(), TenantId = Tenant, Key = "inuse", Name = "In use", CreatedAt = now };
        _customId = custom.Id;
        _builtInId = builtIn.Id;
        _inUseId = inUse.Id;
        db.Roles.AddRange(custom, builtIn, inUse);

        var principal = new Principal { Id = Guid.NewGuid(), Type = PrincipalType.Human, CreatedAt = now };
        db.Principals.Add(principal);
        var membership = new TenantMembership { Id = Guid.NewGuid(), TenantId = Tenant, PrincipalId = principal.Id, Kind = MembershipKind.Customer, CreatedAt = now };
        db.TenantMemberships.Add(membership);
        db.MembershipRoles.Add(new MembershipRole { TenantMembershipId = membership.Id, RoleId = inUse.Id });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private IdentityDbContext NewContext() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(_cs).Options);

    [Fact]
    public async Task Deletes_a_custom_role()
    {
        await using var db = NewContext();
        Assert.Equal(DeleteRoleResult.Deleted, await new RbacManagementService(db).DeleteRoleAsync(_customId, default));

        await using var verify = NewContext();
        Assert.False(await verify.Roles.AnyAsync(r => r.Id == _customId));
    }

    [Fact]
    public async Task Refuses_a_built_in_role()
    {
        await using var db = NewContext();
        Assert.Equal(DeleteRoleResult.BuiltIn, await new RbacManagementService(db).DeleteRoleAsync(_builtInId, default));
    }

    [Fact]
    public async Task Refuses_a_role_still_assigned_to_a_member()
    {
        await using var db = NewContext();
        Assert.Equal(DeleteRoleResult.InUse, await new RbacManagementService(db).DeleteRoleAsync(_inUseId, default));
    }

    [Fact]
    public async Task Unknown_role_is_not_found()
    {
        await using var db = NewContext();
        Assert.Equal(DeleteRoleResult.NotFound, await new RbacManagementService(db).DeleteRoleAsync(Guid.NewGuid(), default));
    }
}
