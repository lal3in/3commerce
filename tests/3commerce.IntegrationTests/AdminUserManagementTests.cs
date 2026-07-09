using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Domain.Tenancy;
using ThreeCommerce.Identity.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Operator user administration (aui_8): the AdminUserService used by the master-admin endpoints —
/// list users in a tenant, reset a password (one-time temp, hash changes, lockout cleared), change an
/// email (normalised + re-verification required). Runs the real service through its tenant scope.
/// </summary>
[Trait("Category", "Integration")]
public class AdminUserManagementTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private static readonly Guid Tenant = Guid.NewGuid();
    private Guid _userId;
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
        var principal = new Principal { Id = Guid.NewGuid(), Type = PrincipalType.Human, CreatedAt = now };
        db.Principals.Add(principal);
        _userId = Guid.CreateVersion7();
        db.Users.Add(new User
        {
            Id = _userId,
            TenantId = Tenant,
            PrincipalId = principal.Id,
            Email = "op@example.test",
            PasswordHash = "ORIGINAL",
            Role = "admin",
            EmailVerified = true,
            FailedLoginCount = 3,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private IdentityDbContext NewContext() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(_cs).Options);

    private static AdminUserService Service(IdentityDbContext db) => new(db, new FakeHasher(), new NoOpAuditRecorder());

    [Fact]
    public async Task Lists_users_in_the_tenant()
    {
        await using var db = NewContext();
        var users = await Service(db).ListAsync(Tenant, default);
        Assert.Contains(users, u => u.Id == _userId && u.Email == "op@example.test" && u.Role == "admin");
    }

    [Fact]
    public async Task Reset_password_issues_temp_changes_hash_and_clears_lockout()
    {
        await using var db = NewContext();
        var temp = await Service(db).ResetPasswordAsync(Tenant, _userId, null, null, default);
        Assert.False(string.IsNullOrWhiteSpace(temp));

        await using var verify = NewContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == _userId);
        Assert.Equal($"H:{temp}", user.PasswordHash);
        Assert.Equal(0, user.FailedLoginCount);
    }

    [Fact]
    public async Task Change_email_normalises_and_forces_reverification()
    {
        await using var db = NewContext();
        Assert.True(await Service(db).ChangeEmailAsync(Tenant, _userId, "  New@Example.test ", null, null, default));

        await using var verify = NewContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == _userId);
        Assert.Equal("new@example.test", user.Email);
        Assert.False(user.EmailVerified);
    }

    [Fact]
    public async Task Reset_unknown_user_returns_null()
    {
        await using var db = NewContext();
        Assert.Null(await Service(db).ResetPasswordAsync(Tenant, Guid.NewGuid(), null, null, default));
    }

    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string password) => $"H:{password}";
        public bool Verify(string password, string encodedHash) => encodedHash == $"H:{password}";
    }

    // No bus in this unit-style integration test — audit recording is a no-op here.
    private sealed class NoOpAuditRecorder : ThreeCommerce.BuildingBlocks.Infrastructure.Audit.IAuditRecorder
    {
        public Task RecordAsync(ThreeCommerce.BuildingBlocks.Infrastructure.Audit.AuditDraft draft, CancellationToken ct) => Task.CompletedTask;
    }
}
