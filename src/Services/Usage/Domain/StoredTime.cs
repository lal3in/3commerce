namespace ThreeCommerce.Usage.Domain;

/// <summary>
/// A timestamp at the precision Postgres stores it (<c>timestamptz</c> keeps microseconds; Npgsql drops a
/// <see cref="DateTimeOffset"/>'s seventh fractional digit, which Linux clocks fill). Usage compares the times it keeps
/// for the auto-load pause with times from Payments' events, so it truncates them when they are set — its own copy, as
/// every service keeps one (shared code is plumbing only; engineering-gotchas, Integration tests).
/// </summary>
public static class StoredTime
{
    /// <summary><paramref name="time"/> truncated to whole microseconds (what a Postgres round trip returns).</summary>
    public static DateTimeOffset Of(DateTimeOffset time) => time.AddTicks(-(time.Ticks % TimeSpan.TicksPerMicrosecond));
}
