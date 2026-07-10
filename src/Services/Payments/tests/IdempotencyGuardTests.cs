using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Idempotency;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// Uniform idempotency wrapper over IdempotencyRecord (plan item 12): a replayed key with the same
/// request returns the stored response and does not run the op again; a replayed key with a
/// different request throws.
/// </summary>
public class IdempotencyGuardTests
{
    private static PaymentsDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseInMemoryDatabase($"idem_{Guid.NewGuid():N}")
            .Options);

    private sealed record Response(string IntentId, long Amount);

    [Fact]
    public async Task Replay_with_the_same_request_returns_the_stored_response_without_rerunning()
    {
        await using var db = NewDb();
        var guard = new IdempotencyGuard(db, TimeProvider.System);
        var request = new { OrderId = "o1", Amount = 4990L };
        var runs = 0;

        Task<Response> Op(CancellationToken ct) { runs++; return Task.FromResult(new Response("pi_1", 4990)); }

        var first = await guard.ExecuteAsync("key-1", request, Op, default);
        var second = await guard.ExecuteAsync("key-1", request, Op, default);

        Assert.Equal(first, second);
        Assert.Equal("pi_1", second.IntentId);
        Assert.Equal(1, runs); // the op ran once; the replay came from the store
    }

    [Fact]
    public async Task Replay_with_a_different_request_for_the_same_key_throws()
    {
        await using var db = NewDb();
        var guard = new IdempotencyGuard(db, TimeProvider.System);

        await guard.ExecuteAsync("key-2", new { OrderId = "o1", Amount = 100L },
            _ => Task.FromResult(new Response("pi", 100)), default);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() => guard.ExecuteAsync(
            "key-2", new { OrderId = "o1", Amount = 999L },
            _ => Task.FromResult(new Response("pi", 999)), default));
    }
}
