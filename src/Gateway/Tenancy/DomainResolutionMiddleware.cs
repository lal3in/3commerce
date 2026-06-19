using Microsoft.Extensions.Options;

namespace ThreeCommerce.Gateway.Tenancy;

/// <summary>
/// Phase-1 tenant/storefront context resolver. In v1 foundation this is config-backed; the
/// future global domain registry will replace the lookup without changing the trusted headers.
/// </summary>
public sealed class DomainResolutionMiddleware(
    RequestDelegate next,
    IOptions<StorefrontDomainOptions> options,
    ILogger<DomainResolutionMiddleware> logger)
{
    public const string TenantHeader = "X-3C-Tenant-Id";
    public const string StorefrontHeader = "X-3C-Storefront-Id";
    public const string CanonicalHostHeader = "X-3C-Canonical-Host";

    public async Task InvokeAsync(HttpContext context)
    {
        // These headers are trusted only when minted by the gateway.
        context.Request.Headers.Remove(TenantHeader);
        context.Request.Headers.Remove(StorefrontHeader);
        context.Request.Headers.Remove(CanonicalHostHeader);

        var mapping = Resolve(context.Request.Host.Host);
        if (mapping is null)
        {
            await next(context);
            return;
        }

        context.Request.Headers[TenantHeader] = mapping.TenantId;
        if (!string.IsNullOrWhiteSpace(mapping.StorefrontId))
        {
            context.Request.Headers[StorefrontHeader] = mapping.StorefrontId;
        }

        if (!string.IsNullOrWhiteSpace(mapping.CanonicalHost))
        {
            context.Request.Headers[CanonicalHostHeader] = mapping.CanonicalHost;
        }

        logger.LogDebug("Resolved host {Host} to tenant {TenantId} storefront {StorefrontId}",
            context.Request.Host.Host, mapping.TenantId, mapping.StorefrontId);
        await next(context);
    }

    private StorefrontDomainMapping? Resolve(string host)
    {
        var configured = options.Value.Domains.FirstOrDefault(d =>
            string.Equals(d.Host, host, StringComparison.OrdinalIgnoreCase));
        if (configured is not null)
        {
            return configured;
        }

        if (string.IsNullOrWhiteSpace(options.Value.DefaultTenantId))
        {
            return null;
        }

        return new StorefrontDomainMapping
        {
            Host = host,
            TenantId = options.Value.DefaultTenantId,
            StorefrontId = options.Value.DefaultStorefrontId,
            Canonical = true,
            CanonicalHost = host,
        };
    }
}
