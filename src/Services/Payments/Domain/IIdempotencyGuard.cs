namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// Uniform idempotency wrapper over <see cref="IdempotencyRecord"/> for payment mutations (plan
/// item 12). A replayed key with the same request returns the stored response instead of acting
/// twice; a replayed key with a DIFFERENT request throws (a client reused a key for another
/// request). Records are per-service (Payments schema), keyed by the provider/idempotency key.
/// </summary>
public interface IIdempotencyGuard
{
    /// <summary>
    /// Returns the stored response for a replayed <paramref name="key"/> (same request hash), else
    /// runs <paramref name="operation"/>, stores its serialized response, and returns it. A hash
    /// mismatch on a known key throws <see cref="IdempotencyConflictException"/>.
    /// </summary>
    public Task<TResponse> ExecuteAsync<TResponse>(
        string key,
        object request,
        Func<CancellationToken, Task<TResponse>> operation,
        CancellationToken ct);
}

/// <summary>Thrown when an idempotency key is replayed with a different request payload.</summary>
public sealed class IdempotencyConflictException(string key)
    : Exception($"Idempotency key '{key}' was reused with a different request.");
