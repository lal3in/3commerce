namespace ThreeCommerce.Workers.Notifications.Email;

/// <summary>
/// TrackingAssigned carries no email; the worker remembers the order→email mapping from
/// OrderConfirmed. v1 keeps it in-memory (best-effort) — a persistent copy is a later refinement.
/// </summary>
public interface IOrderEmailLookup
{
    public void Remember(Guid orderId, string email);
    public Task<string?> EmailForOrderAsync(Guid orderId);
}

public sealed class InMemoryOrderEmailLookup : IOrderEmailLookup
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _map = new();
    public void Remember(Guid orderId, string email) => _map[orderId] = email;
    public Task<string?> EmailForOrderAsync(Guid orderId) => Task.FromResult(_map.TryGetValue(orderId, out var e) ? e : null);
}
