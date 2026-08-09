using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Infrastructure.Sessions;

namespace ThreeCommerce.Identity.Tests;

/// <summary>Test double: a session cache that never caches (every path a no-op), for unit-constructing
/// Identity services that now take an <see cref="ISessionCache"/> (ADR-0044).</summary>
internal sealed class NoopSessionCache : ISessionCache
{
    public Task<SessionInfo?> GetAsync(string tokenHash, CancellationToken ct) => Task.FromResult<SessionInfo?>(null);
    public Task SetAsync(string tokenHash, SessionInfo info, CancellationToken ct) => Task.CompletedTask;
    public Task InvalidateTokenAsync(string tokenHash, Guid userId, CancellationToken ct) => Task.CompletedTask;
    public Task InvalidateUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
}
