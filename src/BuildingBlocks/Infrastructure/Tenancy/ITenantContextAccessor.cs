namespace ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>Ambient access to the current <see cref="TenantContext"/> (set by request/message middleware).</summary>
public interface ITenantContextAccessor
{
    public TenantContext Current { get; set; }
}

/// <summary>AsyncLocal-backed accessor so the context flows through async call chains within a request.</summary>
public sealed class AsyncLocalTenantContextAccessor : ITenantContextAccessor
{
    private static readonly AsyncLocal<TenantContext> Value = new();

    public TenantContext Current
    {
        get => Value.Value ?? TenantContext.None;
        set => Value.Value = value;
    }
}
