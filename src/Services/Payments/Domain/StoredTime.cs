namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// A timestamp at the precision Postgres stores it. <c>timestamptz</c> keeps microseconds; a .NET
/// <see cref="DateTimeOffset"/> carries 100 ns ticks, and Npgsql drops the last digit on write. Linux clocks fill that
/// digit (macOS clocks do not), so an aggregate holding an untruncated time differs from itself once reloaded — and
/// anything derived from it (a renewal's idempotency key <c>renew-{id}-{periodEnd:O}</c>, the pinned amount stored
/// under that key) differs too. Times an aggregate persists and later keys on are truncated here when they are set.
/// </summary>
public static class StoredTime
{
    /// <summary><paramref name="time"/> truncated to whole microseconds (what a Postgres round trip returns).</summary>
    public static DateTimeOffset Of(DateTimeOffset time) => time.AddTicks(-(time.Ticks % TimeSpan.TicksPerMicrosecond));
}
