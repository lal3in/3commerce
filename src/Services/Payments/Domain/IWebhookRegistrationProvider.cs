namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// Optional adapter capability for managing a provider-side webhook endpoint (register / disable). Segregated
/// from <see cref="IPaymentProvider"/> so adapters that don't expose endpoint management aren't forced to
/// implement it. Real calls are mode-gated inside the adapter; the mock core answers deterministically.
/// Used when a payment account is activated/suspended so the endpoint URL + signing secret stay in sync
/// (pay_disp_4) — a changed URL would otherwise silently drop notifications.
/// </summary>
public interface IWebhookRegistrationProvider
{
    /// <summary>(Re)registers the webhook endpoint at <paramref name="url"/>, returning its id + signing secret.</summary>
    public Task<WebhookRegistrationResult> RegisterWebhookAsync(string url, CancellationToken ct);

    /// <summary>Disables a previously-registered endpoint (best-effort; a missing endpoint is a no-op).</summary>
    public Task DisableWebhookAsync(string endpointId, CancellationToken ct);
}

public sealed record WebhookRegistrationResult(string EndpointId, string SigningSecret);
