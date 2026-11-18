namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// Command (operator backfill, <c>payment_customer_email_backfill</c>): the account email of a member who already has a
/// Payments vault (<c>PaymentCustomer</c>) whose email was never recorded. Payments finds a usage charge's default saved
/// card through that email (owner decision 2026-11-06), and before #332 a vault learnt it only when the member opened
/// their saved cards — so an older vault's card was invisible to auto top-ups. Sent point-to-point to Payments' own queue
/// by the backfill tool's <c>payment-customer-email</c> command, which reads the email from Identity; Payments records it
/// only on a vault that has none (never overwrites one), so a redelivery or a re-run is a no-op.
/// </summary>
public record RecordPaymentCustomerEmail(Guid TenantId, Guid UserId, string Email);
