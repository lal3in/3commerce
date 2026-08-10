namespace ThreeCommerce.Payments.Domain.Ledger;

/// <summary>
/// Payments' local read copy of a storefront's OPERATOR-CONFIGURABLE ledger account codes (phase 2,
/// ADR-0008 projection), fed by Catalog's <c>StorefrontConfigChanged</c>: revenue, tax, receivable and
/// shipping income — the income side a store can rename. A sale/refund attributed to a storefront posts
/// its revenue and tax to these accounts (the receivable bridges cash settlement); Catalog stays the
/// owner and editor of the codes. The rest of the storefront's chart (cash/fees per PSP, refunds-contra,
/// COGS, write-offs, supplier/carrier payables) is DERIVED deterministically from the storefront id
/// (Accounts.*StoreFor), not stored here. Per the ledger_sf mandate every attributed movement posts to
/// the store's own accounts — there is no shared/platform-default account (ADR-0044); the shared
/// constants remain only as the forward-only fallback for genuinely unattributed (null-storefront)
/// postings, which the NoSharedAccountPostingTests guard proves attributed paths never hit.
/// </summary>
public sealed class StorefrontLedgerAccounts
{
    public Guid StorefrontId { get; set; }
    public required string ReceivableAccountCode { get; set; }
    public required string RevenueAccountCode { get; set; }
    public required string TaxAccountCode { get; set; }
    /// <summary>Shipping-income account for the store; null on pre-shipping-split projections.</summary>
    public string? ShippingAccountCode { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
