using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Reference;

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
        $"Thanks for your purchase. Order {orderId} for {Money.Amount(amountMinor, currency)} {currency} is confirmed. "
            + $"Track it at {storefrontBaseUrl}/orders/{orderId}.",
        NotificationReferences.OrderConfirmed(orderId));

    public EmailMessage TrackingAssigned(string to, Guid orderId, string carrier, string trackingNumber) => new(
        to,
        "Your order has shipped",
        $"Order {orderId} is on its way via {carrier}. Tracking number: {trackingNumber}.");

    public EmailMessage TicketOpened(string to, Guid ticketId, Guid orderId) => new(
        to,
        "We received your support request",
        $"Your ticket {ticketId} for order {orderId} is open. We'll be in touch shortly.");

    /// <summary>
    /// A subscription renewal the customer's bank wants them to authenticate (3DS/SCA) before it pays (ADR-0057
    /// amendment 2026-10-27). The renewal was not taken and the subscription is past-due until it is. The delivery-log
    /// reference names the subscription period, so "was this customer asked?" has an exact answer.
    /// </summary>
    public EmailMessage SubscriptionRenewalActionRequired(SubscriptionRenewalActionRequired e) => new(
        e.CustomerEmail,
        "Action needed: confirm your subscription payment",
        $"Your bank needs you to confirm the renewal payment of {Money.Amount(e.AmountMinor, e.Currency)} {e.Currency} for your subscription. "
            + "The renewal has not been charged and your subscription is past due until it is. "
            + $"Complete the payment here: {PayLink(e.SubscriptionId)}"
            + Schedule(e.NextRetryAt, e.CancelsAfter),
        NotificationReferences.SubscriptionRenewalActionRequired(e.SubscriptionId, e.PeriodEnd));

    /// <summary>A declined renewal that will be retried (dunning, ADR-0057 amendment 2026-10-27).</summary>
    public EmailMessage SubscriptionPaymentFailed(SubscriptionPaymentFailed e) => new(
        e.CustomerEmail,
        "Your subscription payment didn't go through",
        $"We couldn't charge {Money.Amount(e.AmountMinor, e.Currency)} {e.Currency} for your subscription renewal, so it is past due. "
            + $"Pay now or use another card here: {PayLink(e.SubscriptionId)}"
            + Schedule(e.NextRetryAt, e.CancelsAfter),
        NotificationReferences.SubscriptionPaymentFailed(e.SubscriptionId, e.PeriodEnd, e.NextRetryAt));

    /// <summary>The final scheduled retry did not settle: the subscription is cancelled (ADR-0057 amendment 2026-10-27).</summary>
    public EmailMessage SubscriptionCancelledForNonPayment(SubscriptionCancelledForNonPayment e) => new(
        e.CustomerEmail,
        "Your subscription has been cancelled",
        $"We tried several times but couldn't collect the renewal payment of {Money.Amount(e.AmountMinor, e.Currency)} {e.Currency}, "
            + "so your subscription has been cancelled. You have not been charged for the unpaid period. "
            + $"You can subscribe again at any time at {storefrontBaseUrl}.",
        NotificationReferences.SubscriptionCancelledForNonPayment(e.SubscriptionId, e.PeriodEnd));

    /// <summary>
    /// A metered usage charge (overage or prepaid auto-load) that did not settle (owner decision 2026-10-27,
    /// usage_charge_failure_feedback) — the same retry and email rules as a subscription renewal: each failure says when it
    /// is retried and when billable usage is suspended; the final one says it is suspended. There is no usage pay page yet
    /// (tracker <c>usage_charge_pay_page</c>), so the email links the account.
    /// </summary>
    public EmailMessage UsageChargeFailed(UsageChargeFailed e)
    {
        var amount = $"{Money.Amount(e.AmountMinor, e.Currency)} {e.Currency}";
        var what = e.Kind == UsageChargeKind.AutoLoad
            ? $"your automatic {e.Meter} top-up ({e.Quantity} units)"
            : $"your {e.Meter} usage beyond your plan ({e.Quantity} units)";
        if (e.Final)
        {
            return new(
                e.CustomerEmail,
                $"Your {e.Meter} usage has been suspended",
                $"We tried several times but couldn't collect {amount} for {what}, so billable {e.Meter} usage is suspended until it is paid. "
                    + "Usage within your plan's allowance and any prepaid credit you have left are not affected. "
                    + $"You can manage your account at {storefrontBaseUrl}/account.",
                NotificationReferences.UsageChargeFailed(e.Reference, null));
        }

        var reason = e.Reason == ChargeFailureReason.RequiresAction
            ? $"Your bank asked to confirm the payment of {amount} for {what}, so it was not taken. "
            : $"We couldn't charge {amount} for {what}. ";
        var consequence = e.Kind == UsageChargeKind.AutoLoad
            ? "The prepaid credit from this top-up is on hold until it is paid, and no further automatic top-ups will be made. "
            : "The amount is still due. ";
        return new(
            e.CustomerEmail,
            $"Your {e.Meter} usage payment didn't go through",
            reason + consequence + $"You can manage your account at {storefrontBaseUrl}/account."
                + UsageSchedule(e.Meter.ToString(), e.NextRetryAt, e.AbandonsAfter),
            NotificationReferences.UsageChargeFailed(e.Reference, e.NextRetryAt));
    }

    private static string UsageSchedule(string meter, DateTimeOffset? nextRetryAt, DateTimeOffset? abandonsAfter) =>
        (nextRetryAt is { } next ? $" We will try again on {next.UtcDateTime.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)} (UTC)." : string.Empty)
        + (abandonsAfter is { } end ? $" If it is still unpaid after {end.UtcDateTime.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)} (UTC), billable {meter} usage will be suspended." : string.Empty);

    /// <summary>The storefront "complete payment" page of one subscription (3-D Secure or another card).</summary>
    private string PayLink(Guid subscriptionId) => $"{storefrontBaseUrl}/account/subscriptions/{subscriptionId:D}/pay";

    private static string Schedule(DateTimeOffset? nextRetryAt, DateTimeOffset? cancelsAfter) =>
        (nextRetryAt is { } next ? $" Otherwise we will try again on {next.UtcDateTime.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)} (UTC)." : string.Empty)
        + (cancelsAfter is { } end ? $" If it is still unpaid after {end.UtcDateTime.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)} (UTC), the subscription will be cancelled." : string.Empty);

    public EmailMessage RmaStateChanged(string to, Guid rmaId, string state) => new(
        to,
        $"Your return/refund request is now {state}",
        $"Update on your request {rmaId}: status is now {state}.");

    /// <summary>
    /// TEST-ONLY mock-payment payload email (pay_3, ADR-0039). Clearly labelled, and NEVER sent in
    /// Production (the event is never published there). The redacted payload from
    /// <c>MockPaymentCaptured.RedactedPayloadJson</c> is embedded verbatim — it already excludes
    /// PAN/CVV/wallet tokens and masks provider payment-method refs to last-4.
    /// </summary>
    public EmailMessage MockPayment(string to, MockPaymentCaptured e)
    {
        var amount = Money.Amount(e.AmountMinor, e.Currency);
        var subject = $"[TEST ONLY / MOCK PAYMENT] {e.Operation} order {e.OrderId} — {amount} {e.Currency} via {e.Provider}/{e.MethodKind}";
        var body =
            "================= TEST ONLY / MOCK PAYMENT =================\n" +
            "This email was generated by 3commerce running in a test mode\n" +
            "(LocalMock/Sandbox). NO money moved. It shows the payment request\n" +
            "that WOULD have been sent to the provider. Never sent in Production.\n" +
            "===========================================================\n\n" +
            $"Operation:  {e.Operation}\n" +
            $"Order:      {e.OrderId}\n" +
            $"Tenant:     {e.TenantId}\n" +
            $"Provider:   {e.Provider}    Mode: {e.Mode}\n" +
            $"Method:     {e.MethodKind}\n" +
            $"Scenario:   {e.Scenario}\n" +
            $"Amount:     {amount} {e.Currency}    Intent: {e.PaymentIntentId}\n" +
            $"Captured:   {e.CapturedAt:u}\n\n" +
            "--- Provider request payload (redacted) ---\n" +
            e.RedactedPayloadJson + "\n" +
            "-------------------------------------------\n" +
            "walletToken / PAN / CVV are never included.";
        return new EmailMessage(to, subject, body);
    }
}
