namespace ThreeCommerce.Payments.Domain.Ledger;

/// <summary>
/// Payments' local read copy of a storefront's ledger account codes (phase 2, ADR-0008 projection),
/// fed by Catalog's <c>StorefrontConfigChanged</c>. A sale/refund attributed to a storefront posts its
/// revenue and tax to these accounts (the receivable bridges cash settlement); Catalog stays the owner
/// and editor of the codes. Absent projection → the posting falls back to the shared accounts.
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
