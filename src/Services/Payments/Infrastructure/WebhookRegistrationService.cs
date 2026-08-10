using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>
/// Keeps a payment account's provider-side webhook endpoint in sync with its lifecycle (pay_disp_4). When an
/// account is activated, the provider endpoint is (re)registered at our <c>/webhooks/{provider}</c> URL and
/// the returned signing secret is rotated into <see cref="WebhookSecretService"/> — additive, so the previous
/// secret stays valid during cutover and no in-flight notification is dropped (verification accepts any
/// active secret). On suspend the endpoint is disabled and the tracking cleared. Providers that don't support
/// endpoint management (<see cref="IWebhookRegistrationProvider"/>) are a no-op. All real calls are mode-gated
/// inside the adapter; the mock answers deterministically so the flow is exercised offline / in tests.
/// </summary>
public sealed class WebhookRegistrationService(
    IPaymentProviderRegistry registry,
    WebhookSecretService secrets,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<WebhookRegistrationService> logger)
{
    public async Task SyncOnActivateAsync(PaymentAccount account, CancellationToken ct)
    {
        // Already registered for this activation cycle — nothing to re-sync until it's suspended.
        if (!string.IsNullOrEmpty(account.WebhookEndpointId))
        {
            return;
        }

        if (registry.Resolve(account.ToSnapshot()) is not IWebhookRegistrationProvider provider)
        {
            return; // provider doesn't manage endpoints (e.g. merchant-of-record)
        }

        var url = WebhookUrlFor(account.Provider);
        var registration = await provider.RegisterWebhookAsync(url, ct);

        // Rotate the new secret in (active); prior secrets stay active until an operator retires them.
        await secrets.CreateAsync(account.Provider, registration.SigningSecret, $"auto: {account.Name} activated", ct);
        account.RecordWebhookRegistration(registration.EndpointId, url, time.GetUtcNow());
        logger.LogInformation("Registered webhook endpoint {Endpoint} for account {Account} at {Url}", registration.EndpointId, account.Id, url);
    }

    public async Task DisableOnSuspendAsync(PaymentAccount account, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(account.WebhookEndpointId))
        {
            return;
        }

        if (registry.Resolve(account.ToSnapshot()) is IWebhookRegistrationProvider provider)
        {
            await provider.DisableWebhookAsync(account.WebhookEndpointId, ct);
        }

        logger.LogInformation("Disabled webhook endpoint {Endpoint} for account {Account}", account.WebhookEndpointId, account.Id);
        account.ClearWebhookRegistration(time.GetUtcNow());
    }

    private string WebhookUrlFor(string provider)
    {
        // The public base URL of the gateway (which forwards /webhooks/{provider} verbatim to Payments).
        var baseUrl = (configuration["Payments:WebhookBaseUrl"] ?? "http://localhost:8080").TrimEnd('/');
        return $"{baseUrl}/webhooks/{provider.Trim().ToLowerInvariant()}";
    }
}
