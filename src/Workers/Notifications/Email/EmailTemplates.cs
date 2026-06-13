namespace ThreeCommerce.Workers.Notifications.Email;

/// <summary>
/// Versioned-in-repo templates. The storefront base URL is configured so links
/// point at the public site, not internal services.
/// </summary>
public sealed class EmailTemplates(string storefrontBaseUrl)
{
    public EmailMessage Verification(string to, string token) => new(
        to,
        "Verify your email address",
        $"Welcome to 3commerce. Confirm your address: {storefrontBaseUrl}/verify-email?token={token}");

    public EmailMessage PasswordReset(string to, string token) => new(
        to,
        "Reset your password",
        $"Reset your password here (expires in 1 hour): {storefrontBaseUrl}/reset-password?token={token}");

    public EmailMessage OrderConfirmed(string to, Guid orderId, long amountMinor, string currency) => new(
        to,
        "Your order is confirmed",
        $"Thanks for your purchase. Order {orderId} for {amountMinor / 100m:0.00} {currency} is confirmed. "
            + $"Track it at {storefrontBaseUrl}/orders/{orderId}.");
}
