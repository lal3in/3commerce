using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>
/// Webhook signing-secret registry (def_2 / mt6_7). Resolution order: active registry secrets
/// newest-first, then the legacy config key as a fallback so dev keeps working with zero rows.
/// Rotation: create the new secret, let the provider cut over (both verify), deactivate the old.
/// </summary>
public sealed class WebhookSecretService(PaymentsDbContext db, IConfiguration configuration, TimeProvider time)
{
    /// <summary>Config fallback per provider — the pre-registry single-secret keys.</summary>
    private static readonly Dictionary<string, string> ConfigFallbacks = new()
    {
        ["stripe"] = "Stripe:WebhookSecret",
    };

    public async Task<IReadOnlyList<string>> GetActiveSecretsAsync(string provider, CancellationToken ct)
    {
        var normalized = provider.Trim().ToLowerInvariant();
        var secrets = await db.WebhookSecrets
            .Where(s => s.Provider == normalized && s.Active)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.Secret)
            .ToListAsync(ct);
        if (secrets.Count > 0)
        {
            return secrets;
        }

        var fallback = ConfigFallbacks.TryGetValue(normalized, out var key) ? configuration[key] : null;
        return string.IsNullOrEmpty(fallback) ? [] : [fallback];
    }

    public async Task<List<WebhookSecret>> ListAsync(string? provider, CancellationToken ct)
    {
        var query = db.WebhookSecrets.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(provider))
        {
            var normalized = provider.Trim().ToLowerInvariant();
            query = query.Where(s => s.Provider == normalized);
        }

        return await query.OrderBy(s => s.Provider).ThenByDescending(s => s.CreatedAt).ToListAsync(ct);
    }

    public async Task<WebhookSecret> CreateAsync(string provider, string secret, string? label, CancellationToken ct)
    {
        var entity = new WebhookSecret
        {
            Id = Guid.CreateVersion7(),
            Provider = provider.Trim().ToLowerInvariant(),
            Secret = secret,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            CreatedAt = time.GetUtcNow(),
        };
        db.WebhookSecrets.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<bool> DeactivateAsync(Guid id, CancellationToken ct)
    {
        var entity = await db.WebhookSecrets.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null || !entity.Active)
        {
            return false;
        }

        entity.Active = false;
        entity.DeactivatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }
}
