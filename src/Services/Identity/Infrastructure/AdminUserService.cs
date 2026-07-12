using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.Identity.Infrastructure;

/// <summary>
/// Operator user administration (aui_8): list users in a tenant, reset a user's password (issues a
/// one-time temporary password the operator passes on), and change a user's email (re-verification
/// required). Writes go through the tenant RLS scope (ADR-0024), like AuthService.
///
/// NOTE: gated by the admin role + an explicit tenantId today. True cross-tenant *global master* control
/// should additionally require MasterGlobal (platform scope) — tracked as a follow-up.
/// </summary>
public sealed class AdminUserService(IdentityDbContext db, IPasswordHasher passwordHasher, IAuditRecorder audit)
{
    public async Task<List<AdminUserDto>> ListAsync(Guid tenantId, CancellationToken ct)
    {
        await using var scope = await db.BeginTenantScopeAsync(TenantContext.ForTenant(tenantId), ct);
        var users = await db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId)
            .OrderBy(u => u.Email)
            .Select(u => new AdminUserDto(u.Id, u.Email, u.Role, u.EmailVerified, u.FirstName, u.LastName, u.CreatedAt))
            .ToListAsync(ct);
        await scope.CommitAsync(ct);
        return users;
    }

    /// <summary>Sets a fresh temporary password and returns it (shown once to the operator). Clears lockout.</summary>
    public async Task<string?> ResetPasswordAsync(Guid tenantId, Guid userId, Guid? actorId, string? actorRole, CancellationToken ct)
    {
        await using var scope = await db.BeginTenantScopeAsync(TenantContext.ForTenant(tenantId), ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, ct);
        if (user is null)
        {
            return null;
        }

        var temporary = GenerateTemporaryPassword();
        user.PasswordHash = passwordHasher.Hash(temporary);
        user.FailedLoginCount = 0;
        // Records who reset whose password — never the password itself (mt6_2 PII-safety).
        await audit.RecordAsync(AuditCategories.Mutation(
            tenantId, actorId, actorRole, "User", userId.ToString(), "identity.user.reset_password"), ct);
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return temporary;
    }

    /// <summary>Changes the user's email and marks it unverified (a fresh verification must follow).</summary>
    public async Task<bool> ChangeEmailAsync(Guid tenantId, Guid userId, string newEmail, Guid? actorId, string? actorRole, CancellationToken ct)
    {
        var normalized = newEmail.Trim().ToLowerInvariant();
        await using var scope = await db.BeginTenantScopeAsync(TenantContext.ForTenant(tenantId), ct);
        if (await db.Users.AnyAsync(u => u.TenantId == tenantId && u.Email == normalized && u.Id != userId, ct))
        {
            await scope.CommitAsync(ct);
            return false; // email already in use in this tenant
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, ct);
        if (user is null)
        {
            await scope.CommitAsync(ct);
            return false;
        }

        user.Email = normalized;
        user.EmailVerified = false;
        await audit.RecordAsync(AuditCategories.Mutation(
            tenantId, actorId, actorRole, "User", userId.ToString(), "identity.user.change_email"), ct);
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return true;
    }

    private static string GenerateTemporaryPassword()
    {
        // 18 url-safe chars — strong, and readable enough to read out once.
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(18);
        return string.Concat(bytes.Select(b => alphabet[b % alphabet.Length]));
    }
}

public record AdminUserDto(Guid Id, string Email, string Role, bool EmailVerified, string? GivenName, string? FamilyName, DateTimeOffset CreatedAt);
