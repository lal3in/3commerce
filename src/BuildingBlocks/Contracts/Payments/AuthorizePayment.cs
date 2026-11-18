namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// Request/response (not fire-and-forget): the checkout endpoint asks Payments to
/// create a payment intent, and blocks only until the intent exists (api.md §3).
/// NetMinor is the total to charge (items + shipping + tax); TaxMinor is the tax portion of it,
/// carried through so the sale can be booked as net revenue + a tax liability (per-storefront books).
/// </summary>
public record AuthorizePayment(
    Guid OrderId,
    long NetMinor,
    string Currency,
    string IdempotencyKey,
    Guid? UserId = null,
    Guid? SavedPaymentMethodId = null,
    bool SavePaymentMethod = false,
    string? ShipCountry = null,
    // Checkout's normalized paymentOption (Stripe|CreditCard|ApplePay|GooglePay|PayPal). Payments maps
    // it to the numeric PaymentMethodKind server-side (pay_4/ADR-0039); wallets settle through the PSP.
    string? PaymentOption = null,
    // The storefront this order belongs to (phase 2). Persisted on the Payment so the sale posts to the
    // storefront's own revenue/tax ledger accounts (per-storefront books). Optional → back-compatible.
    Guid? StorefrontId = null,
    // Tax portion of NetMinor (computed by Ordering at checkout). Persisted on the Payment so the ledger
    // splits revenue (net) from tax collected (liability) instead of booking the whole gross as revenue.
    long TaxMinor = 0,
    // Shipping portion of NetMinor. Persisted on the Payment so the ledger books shipping as its own
    // income line (income.shipping), separate from product revenue. Optional → back-compatible.
    long ShippingMinor = 0,
    // The order's tenant (owner decision 2026-11-15, ADR-0024 amendment): Payments reads the member's payment customer
    // and saved card under FORCE RLS in THIS tenant's scope. Optional → back-compatible: a message from an Ordering
    // that predates it falls back to the host's default tenant.
    Guid? TenantId = null,
    // ADR-0063 § 16, per-destination OSS tax accounts: the part of TaxMinor charged under the Union OSS scheme, by Member
    // State of consumption (Σ ≤ TaxMinor; the rest is the store's domestic tax). Persisted on the Payment so the sale posts
    // each destination's VAT to its own liability account and refunds reverse from the same ones. A concrete array so
    // MassTransit materializes it. Null = no OSS part (every non-EU and domestic sale) → back-compatible.
    OssTaxDestination[]? OssTax = null);

/// <summary>One Member State's OSS VAT in a charge (ADR-0063 § 16): ISO alpha-2 (<c>XI</c> for Northern Ireland goods) and the tax in minor units.</summary>
public record OssTaxDestination(string Country, long TaxMinor);

public record AuthorizePaymentResult(string PaymentIntentId, string ClientSecret, long GrossMinor, long TaxMinor);
