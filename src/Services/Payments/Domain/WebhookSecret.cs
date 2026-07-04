namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// A provider webhook signing secret (mt6_7 registry half, def_2). Platform-scoped — inbound
/// webhooks carry no tenant. The secret must be recoverable to verify HMAC signatures, so it is
/// stored as pasted from the provider dashboard; admin surfaces only ever see <see cref="Masked"/>.
/// Rotation = add the new secret, keep the old one active until the provider cut-over, then
/// deactivate it — verification tries every active secret, newest first.
/// </summary>
public class WebhookSecret
{
    public Guid Id { get; init; }

    /// <summary>Lowercase provider key matching the /webhooks/{provider} route (e.g. "stripe").</summary>
    public required string Provider { get; init; }

    public required string Secret { get; init; }

    /// <summary>Operator note, e.g. "live key rotated 2026-07".</summary>
    public string? Label { get; set; }

    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DeactivatedAt { get; set; }

    /// <summary>The only representation that leaves the service: first 4 + last 4 characters.</summary>
    public string Masked => Secret.Length <= 8 ? "********" : $"{Secret[..4]}…{Secret[^4..]}";
}
