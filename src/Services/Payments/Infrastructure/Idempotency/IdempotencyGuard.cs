using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure.Idempotency;

/// <summary>
/// EF-backed <see cref="IIdempotencyGuard"/> over <see cref="IdempotencyRecord"/> (plan item 12).
/// A replayed key with the same request hash returns the stored response; a replayed key with a
/// different request throws <see cref="IdempotencyConflictException"/>. The stored response is the
/// serialized operation result; the hash is a stable JSON digest of the request.
/// </summary>
public sealed class IdempotencyGuard(PaymentsDbContext db, TimeProvider time) : IIdempotencyGuard
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<TResponse> ExecuteAsync<TResponse>(
        string key,
        object request,
        Func<CancellationToken, Task<TResponse>> operation,
        CancellationToken ct)
    {
        var hash = Hash(request);
        var existing = await db.IdempotencyRecords.FindAsync([key], ct);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
            {
                throw new IdempotencyConflictException(key);
            }

            return JsonSerializer.Deserialize<TResponse>(existing.ResponseJson, SerializerOptions)!;
        }

        var response = await operation(ct);

        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Key = key,
            RequestHash = hash,
            ResponseJson = JsonSerializer.Serialize(response, SerializerOptions),
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);

        return response;
    }

    private static string Hash(object request)
    {
        var json = JsonSerializer.Serialize(request, SerializerOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
