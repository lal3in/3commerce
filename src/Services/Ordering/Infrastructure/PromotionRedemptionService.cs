using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure;

/// <summary>
/// Owns the coupon redemption lifecycle (ADR-0052): RESERVE at checkout, CONFIRM on order confirmation,
/// RELEASE on cancellation / payment failure / checkout expiry.
/// <para>
/// <b>Why reserve at checkout.</b> The discounted amount is what the payment provider authorizes, so the
/// coupon has to be locked in at that moment. Reserving at confirmation instead would let two shoppers
/// both be charged a "last one" 50%-off price and only one of them be honoured.
/// </para>
/// <para>
/// <b>How the cap is race-safe.</b> The total-redemptions cap is enforced by ONE conditional statement —
/// <c>UPDATE PromotionCopies SET RedeemedCount = RedeemedCount + 1 WHERE PromotionId = … AND
/// (MaxRedemptions IS NULL OR RedeemedCount &lt; MaxRedemptions)</c> — whose rows-affected is the answer.
/// Postgres takes a row lock for the duration, so concurrent checkouts serialize on that row and the
/// loser's re-evaluated predicate sees the winner's increment: the cap cannot be exceeded no matter how
/// many requests arrive at once. A read-then-write ("count, then insert") would not hold, which is why
/// there is a counter column at all. The per-customer limit has no single row to serialize on, so it
/// takes a transaction-scoped ADVISORY LOCK keyed by (promotion, customer) before its count — the same
/// mutual exclusion, over a key rather than a row.
/// </para>
/// <para>
/// <b>Idempotency.</b> A unique index on (PromotionId, OrderId) means a retried checkout for the same
/// order can never take a second redemption; confirm and release are status-guarded UPDATEs, so a
/// redelivered message changes nothing.
/// </para>
/// </summary>
public sealed class PromotionRedemptionService(OrderingDbContext db, ILogger<PromotionRedemptionService> logger)
{
    /// <summary>
    /// How long a reservation may sit un-attached before the second-chance sweep may reclaim it. Matches
    /// the checkout saga's 30-minute expiry with a margin: past this point a Reserved row whose checkout
    /// attempt never materialized is the residue of a crash between the reservation's commit and the
    /// attempt's, and holding it forever would burn a limited code for nobody.
    /// </summary>
    private static readonly TimeSpan StaleReservation = TimeSpan.FromMinutes(45);

    /// <summary>
    /// Resolves an entered coupon code to its promotion and says whether — and why not — it applies.
    /// The single entry point BOTH the cart preview and checkout use, so the two can never disagree.
    /// <para>
    /// The lookup is by (tenant, code) WITHOUT the storefront/active/window filters, so a real code aimed
    /// at another store reports <see cref="CouponStatus.WrongStorefront"/> rather than pretending it does
    /// not exist. When the promotion LOOKS exhausted its stale holds are reclaimed first
    /// (<see cref="ReleaseStaleAsync"/>) and the counter re-read: without that, a hold stranded by a crash
    /// would refuse the coupon here forever and the claim path's own sweep could never be reached.
    /// That corrective write is the reason a read-only preview may write — it is gated on the promotion
    /// actually being at its cap, so the common path never touches it.
    /// </para>
    /// </summary>
    public async Task<(PromotionCopy? Promotion, CouponEvaluation Evaluation)> ResolveCouponAsync(
        string? enteredCode,
        Guid tenantId,
        Guid storefrontId,
        string currency,
        IReadOnlyList<PromotionLine> lines,
        string? customerKey,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var code = CouponValidator.Normalize(enteredCode);
        if (code is null)
        {
            return (null, CouponEvaluation.None);
        }

        var promotion = await db.PromotionCopies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Code == code, ct);
        if (promotion is { MaxRedemptions: { } max } && promotion.RedeemedCount >= max
            && await ReleaseStaleAsync(promotion.PromotionId, now, ct) > 0)
        {
            promotion = await db.PromotionCopies.AsNoTracking()
                .FirstOrDefaultAsync(p => p.PromotionId == promotion.PromotionId, ct);
        }

        var customerHeld = promotion is null || customerKey is null
            ? 0
            : await db.PromotionRedemptions.AsNoTracking().CountAsync(
                r => r.PromotionId == promotion.PromotionId
                    && r.CustomerKey == customerKey
                    && r.Status != PromotionRedemptionStatus.Released,
                ct);

        var evaluation = CouponValidator.Evaluate(
            code, promotion, lines, tenantId, storefrontId, currency, now,
            promotion?.RedeemedCount ?? 0, customerHeld);
        return (promotion, evaluation);
    }

    /// <summary>
    /// Reserves one redemption of <paramref name="promotion"/> for this order, or reports which limit
    /// refused it. Returns <see cref="CouponStatus.Applied"/> when the hold is taken (including when this
    /// order already held one — reservation is idempotent per order).
    /// </summary>
    public async Task<CouponStatus> TryReserveAsync(
        PromotionCopy promotion, Guid tenantId, Guid orderId, string customerKey, DateTimeOffset now, CancellationToken ct)
    {
        var promotionId = promotion.PromotionId;

        // No limits at all: nothing to ration, but still RECORD the redemption, so usage reporting and a
        // later tightening of the limit both see the truth.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var existing = await db.PromotionRedemptions
            .FirstOrDefaultAsync(r => r.PromotionId == promotionId && r.OrderId == orderId, ct);
        if (existing is not null)
        {
            if (existing.IsHeld)
            {
                await tx.CommitAsync(ct);
                return CouponStatus.Applied;
            }

            // A previously RELEASED hold for this same order is being retried (the shopper failed payment
            // and came back). Re-take it through the same guards rather than resurrecting it blindly.
            db.PromotionRedemptions.Remove(existing);
            await db.SaveChangesAsync(ct);
        }

        if (promotion.MaxRedemptionsPerCustomer is { } perCustomerLimit)
        {
            // Serialize every concurrent check for THIS (promotion, customer) pair. Without it two
            // simultaneous guest checkouts on the same email both read "0 used" and both insert.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({promotionId.ToString()}), hashtext({customerKey}))", ct);

            var heldForCustomer = await db.PromotionRedemptions.CountAsync(
                r => r.PromotionId == promotionId
                    && r.CustomerKey == customerKey
                    && r.Status != PromotionRedemptionStatus.Released,
                ct);
            if (heldForCustomer >= perCustomerLimit)
            {
                await tx.RollbackAsync(ct);
                return CouponStatus.CustomerLimitReached;
            }
        }

        var claimed = await ClaimAsync(promotionId, ct);
        if (claimed == 0 && promotion.MaxRedemptions is not null)
        {
            // Second chance, for a hold that went stale between validation and this claim: reclaim
            // reservations that were committed but whose checkout attempt never was.
            if (await ReleaseStaleAsync(promotionId, now, ct) > 0)
            {
                claimed = await ClaimAsync(promotionId, ct);
            }
        }

        if (claimed == 0)
        {
            await tx.RollbackAsync(ct);
            return CouponStatus.UsageLimitReached;
        }

        db.PromotionRedemptions.Add(new PromotionRedemption
        {
            Id = Guid.CreateVersion7(),
            PromotionId = promotionId,
            TenantId = tenantId,
            OrderId = orderId,
            CustomerKey = customerKey,
            Code = promotion.Code ?? string.Empty,
            Status = PromotionRedemptionStatus.Reserved,
            ReservedAt = now,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return CouponStatus.Applied;
    }

    /// <summary>
    /// THE race-safe step: one conditional UPDATE whose rows-affected says whether the cap allowed the
    /// claim. Never split into a SELECT and an UPDATE.
    /// </summary>
    private Task<int> ClaimAsync(Guid promotionId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE ordering."PromotionCopies"
             SET "RedeemedCount" = "RedeemedCount" + 1
             WHERE "PromotionId" = {promotionId}
               AND ("MaxRedemptions" IS NULL OR "RedeemedCount" < "MaxRedemptions")
             """,
            ct);

    /// <summary>
    /// Releases reservations older than <see cref="StaleReservation"/> that never became a checkout
    /// attempt or an order, and gives their holds back to the counter. Returns the number of promotion
    /// rows whose counter was corrected (0 = nothing was stale).
    /// <para>
    /// This is the ONLY thing that can recover the crash window between the reservation's commit and the
    /// checkout attempt's, so it must run BEFORE anything reads the counter to refuse a coupon — not only
    /// on the claim path. Callers gate it on the promotion actually looking exhausted, so the common case
    /// pays nothing: a live checkout, and a promotion with allowance left, never touch it.
    /// </para>
    /// </summary>
    public async Task<int> ReleaseStaleAsync(Guid promotionId, DateTimeOffset now, CancellationToken ct)
    {
        var swept = await SweepStaleAsync(promotionId, now, ct);
        if (swept > 0)
        {
            logger.LogWarning(
                "Coupon {PromotionId}: released stale reservation(s) with no checkout attempt or order", promotionId);
        }

        return swept;
    }

    private Task<int> SweepStaleAsync(Guid promotionId, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - StaleReservation;
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             WITH stale AS (
                 UPDATE ordering."PromotionRedemptions" r
                 SET "Status" = 'Released', "ReleasedAt" = {now}
                 WHERE r."PromotionId" = {promotionId}
                   AND r."Status" = 'Reserved'
                   AND r."ReservedAt" < {cutoff}
                   AND NOT EXISTS (SELECT 1 FROM ordering."CheckoutAttempts" a WHERE a."Id" = r."OrderId")
                   AND NOT EXISTS (SELECT 1 FROM ordering."Orders" o WHERE o."Id" = r."OrderId")
                 RETURNING 1
             )
             UPDATE ordering."PromotionCopies"
             SET "RedeemedCount" = GREATEST("RedeemedCount" - (SELECT count(*) FROM stale), 0)
             WHERE "PromotionId" = {promotionId}
               AND (SELECT count(*) FROM stale) > 0
             """,
            ct);
    }

    /// <summary>
    /// Confirms every reservation this order holds — the coupon is now spent for good. Status-guarded, so
    /// a redelivered CheckoutCompleted is a no-op and the counter is never touched twice.
    /// </summary>
    public Task<int> ConfirmAsync(Guid orderId, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE ordering."PromotionRedemptions"
             SET "Status" = 'Confirmed', "ConfirmedAt" = {now}
             WHERE "OrderId" = {orderId} AND "Status" = 'Reserved'
             """,
            ct);

    /// <summary>
    /// Releases every reservation this order holds and gives the count back, so an abandoned or failed
    /// checkout does not burn a limited code. Status-guarded: only a RESERVED row is released (a confirmed
    /// redemption stays spent), and a redelivered OrderCancelled decrements nothing a second time.
    /// </summary>
    public async Task<int> ReleaseAsync(Guid orderId, DateTimeOffset now, CancellationToken ct)
    {
        var released = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             WITH released AS (
                 UPDATE ordering."PromotionRedemptions"
                 SET "Status" = 'Released', "ReleasedAt" = {now}
                 WHERE "OrderId" = {orderId} AND "Status" = 'Reserved'
                 RETURNING "PromotionId"
             )
             UPDATE ordering."PromotionCopies" p
             SET "RedeemedCount" = GREATEST(p."RedeemedCount" - (
                     SELECT count(*) FROM released r WHERE r."PromotionId" = p."PromotionId"), 0)
             WHERE p."PromotionId" IN (SELECT "PromotionId" FROM released)
             """,
            ct);
        if (released > 0)
        {
            logger.LogInformation("Coupon redemption released for order {OrderId}", orderId);
        }

        return released;
    }
}
