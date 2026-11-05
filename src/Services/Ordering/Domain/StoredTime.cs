namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// A timestamp at the precision Postgres stores it. <c>timestamptz</c> keeps microseconds; a .NET
/// <see cref="DateTimeOffset"/> carries 100 ns ticks, and Npgsql drops the last digit on write. Linux clocks fill that
/// digit (macOS clocks do not), so an order holding an untruncated time differs from itself once reloaded — and the
/// <c>OrderConfirmed.CapturedAt</c> published from the in-memory order differs from the one the operator backfill
/// (<c>tools/order-confirmed-backfill</c>, ADR-0060) rebuilds from the row. Times that reach a published event are
/// truncated here when they are set. (Payments has its own copy — shared code is plumbing only.)
/// </summary>
public static class StoredTime
{
    /// <summary><paramref name="time"/> truncated to whole microseconds (what a Postgres round trip returns).</summary>
    public static DateTimeOffset Of(DateTimeOffset time) => time.AddTicks(-(time.Ticks % TimeSpan.TicksPerMicrosecond));
}
