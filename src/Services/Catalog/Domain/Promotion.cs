namespace ThreeCommerce.Catalog.Domain;

/// <summary>What a promotion measures and what it discounts (ADR-0051).</summary>
public enum PromotionScope
{
    /// <summary>Measures the whole cart's item value/quantity and discounts every item line.</summary>
    Storefront = 1,

    /// <summary>Measures one product's item value/quantity and discounts only that product's lines.</summary>
    Product = 2,
}

/// <summary>Lifecycle of a promotion; only <see cref="Active"/> promotions can ever apply.</summary>
public enum PromotionStatus
{
    /// <summary>The promotion participates in evaluation (subject to its window and scope).</summary>
    Active = 1,

    /// <summary>The promotion is switched off and never applies.</summary>
    Inactive = 2,
}

/// <summary>
/// A threshold-based promotion (ADR-0051): a tenant-authored rule that grants free shipping and/or a
/// discount once the cart (storefront scope) or one product's lines (product scope) clear a money
/// threshold and/or a quantity threshold. When both thresholds are set they are <b>AND</b>ed.
/// <para>
/// Thresholds and the fixed discount amount are integer minor units denominated in this promotion's
/// <see cref="Currency"/> and are never converted (strict no-FX posture, ADR-0041) — a EUR promotion
/// simply never applies to an AUD cart. The measurement base is the offer-resolved effective selling
/// price × quantity, excluding tax, shipping and fees (ADR-0047/0048); it is evaluated in Ordering
/// against the projected <c>PromotionCopy</c> read model, never by querying Catalog (ADR-0008).
/// </para>
/// </summary>
public sealed class Promotion
{
    /// <summary>Identity (UUIDv7).</summary>
    public Guid Id { get; init; }

    /// <summary>Owning tenant; a promotion never crosses tenants.</summary>
    public Guid TenantId { get; init; }

    /// <summary>Restricts the promotion to one storefront. Null = every storefront of <see cref="Currency"/>.</summary>
    public Guid? StorefrontId { get; private set; }

    /// <summary>Shopper-visible label shown on the cart/checkout summary and in admin.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Optional coupon code (ADR-0052). Set ⇒ the promotion is CODE-GATED: it applies only when the
    /// shopper enters this code at checkout. Null/empty ⇒ automatic (the ADR-0051 behaviour, unchanged).
    /// Stored trimmed and UPPERCASE; matched case-insensitively. Unique per tenant among non-null codes
    /// (a filtered unique index), so one code never resolves to two promotions.
    /// </summary>
    public string? Code { get; private set; }

    /// <summary>
    /// Total redemptions this promotion may ever grant across all shoppers; null = unlimited.
    /// Single-use is simply 1. Enforced in Ordering at RESERVE time (checkout), because the charged
    /// amount and the payment authorization are both fixed there (ADR-0052).
    /// </summary>
    public int? MaxRedemptions { get; private set; }

    /// <summary>
    /// Redemptions one customer may take; null = unlimited. The customer key is the authenticated user
    /// id when present, else the normalized checkout email, so guest checkouts still count (ADR-0052).
    /// </summary>
    public int? MaxRedemptionsPerCustomer { get; private set; }

    /// <summary>Whether this promotion only applies when the shopper enters <see cref="Code"/>.</summary>
    public bool IsCouponGated => !string.IsNullOrEmpty(Code);

    /// <summary>ISO-4217 code. Thresholds and fixed amounts are denominated in THIS currency (no FX).</summary>
    public required string Currency { get; init; }

    /// <summary>Whether the promotion measures/discounts the whole cart or a single product's lines.</summary>
    public PromotionScope Scope { get; private set; }

    /// <summary>The measured/discounted product. Required iff <see cref="Scope"/> is Product; null otherwise.</summary>
    public Guid? ProductId { get; private set; }

    /// <summary>Money threshold in minor units on the scope's item value; 0 = no money threshold.</summary>
    public long MinimumAmountMinor { get; private set; }

    /// <summary>Quantity threshold on the scope's unit count; 0 = no quantity threshold. Both set ⇒ AND.</summary>
    public int MinimumQuantity { get; private set; }

    /// <summary>Whether winning this promotion zeroes the cart's shipping charge.</summary>
    public bool GrantsFreeShipping { get; private set; }

    /// <summary>Percentage off the scope base, 0–100. Mutually exclusive with <see cref="DiscountAmountMinor"/>.</summary>
    public int PercentOff { get; private set; }

    /// <summary>Fixed discount in minor units, clamped to the scope base. Mutually exclusive with <see cref="PercentOff"/>.</summary>
    public long DiscountAmountMinor { get; private set; }

    /// <summary>True = stacks with other combinable promotions; false = Exclusive (only this one applies).</summary>
    public bool Combinable { get; private set; }

    /// <summary>Inclusive start of the active window (UTC); null = open-ended (always started).</summary>
    public DateTimeOffset? ActiveFrom { get; private set; }

    /// <summary>Inclusive end of the active window (UTC); null = open-ended (never expires).</summary>
    public DateTimeOffset? ActiveUntil { get; private set; }

    /// <summary>Lifecycle status; only Active promotions ever apply.</summary>
    public PromotionStatus Status { get; private set; } = PromotionStatus.Active;

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last-mutation timestamp (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Convenience: whether <see cref="Status"/> is <see cref="PromotionStatus.Active"/>.</summary>
    public bool IsActive => Status == PromotionStatus.Active;

    private Promotion() { }

    /// <summary>
    /// Creates a promotion with its identity, scope and currency. Thresholds, rewards, combinability,
    /// storefront scope and the active window are set through the dedicated setters afterwards; a
    /// freshly created promotion is not yet valid to publish until it carries at least one threshold and
    /// at least one reward (enforced by <see cref="SetThreshold"/> / <see cref="SetReward"/>).
    /// </summary>
    public static Promotion Create(
        Guid tenantId, string name, string currency, PromotionScope scope, Guid? productId, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty)
        {
            throw new CatalogRuleException("Promotion tenant is required.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CatalogRuleException("Promotion name is required.");
        }

        if (name.Trim().Length > 120)
        {
            throw new CatalogRuleException("Promotion name must be 120 characters or fewer.");
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3)
        {
            throw new CatalogRuleException("Promotion currency must be a 3-letter ISO-4217 code.");
        }

        if (!Enum.IsDefined(scope))
        {
            throw new CatalogRuleException("Promotion scope must be valid.");
        }

        var product = productId == Guid.Empty ? null : productId;
        if (scope == PromotionScope.Product && product is null)
        {
            throw new CatalogRuleException("A product-scoped promotion requires a product.");
        }

        if (scope == PromotionScope.Storefront && product is not null)
        {
            throw new CatalogRuleException("A storefront-scoped promotion must not target a product.");
        }

        return new Promotion
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = name.Trim(),
            Currency = currency.Trim().ToUpperInvariant(),
            Scope = scope,
            ProductId = product,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Sets the money and/or quantity threshold. At least one must be positive; when both are, they are
    /// ANDed at evaluation time (ADR-0051). Amounts are minor units in this promotion's currency and are
    /// measured on the item value excluding tax, shipping and fees.
    /// </summary>
    public void SetThreshold(long minimumAmountMinor, int minimumQuantity, DateTimeOffset now)
    {
        if (minimumAmountMinor < 0)
        {
            throw new CatalogRuleException("Promotion minimum amount cannot be negative.");
        }

        if (minimumQuantity < 0)
        {
            throw new CatalogRuleException("Promotion minimum quantity cannot be negative.");
        }

        // A CODE-GATED promotion (ADR-0052) may have no threshold at all — the coupon code IS the gate,
        // and "10% off, no minimum spend, with code WELCOME10" is the commonest coupon there is. An
        // AUTOMATIC promotion still needs one, or it would apply to every cart unconditionally.
        if (minimumAmountMinor == 0 && minimumQuantity == 0 && !IsCouponGated)
        {
            throw new CatalogRuleException("A promotion requires a minimum amount or a minimum quantity.");
        }

        MinimumAmountMinor = minimumAmountMinor;
        MinimumQuantity = minimumQuantity;
        UpdatedAt = now;
    }

    /// <summary>
    /// Sets the reward: free shipping and/or a discount that is either a percentage (0–100) or a fixed
    /// minor-unit amount — never both. At least one reward must be granted.
    /// </summary>
    public void SetReward(bool grantsFreeShipping, int percentOff, long discountAmountMinor, DateTimeOffset now)
    {
        if (percentOff is < 0 or > 100)
        {
            throw new CatalogRuleException("Promotion percent off must be between 0 and 100.");
        }

        if (discountAmountMinor < 0)
        {
            throw new CatalogRuleException("Promotion discount amount cannot be negative.");
        }

        if (percentOff > 0 && discountAmountMinor > 0)
        {
            throw new CatalogRuleException("A promotion sets either a percent off or a fixed discount amount, not both.");
        }

        if (!grantsFreeShipping && percentOff == 0 && discountAmountMinor == 0)
        {
            throw new CatalogRuleException("A promotion requires a reward: free shipping, a percent off, or a fixed discount.");
        }

        GrantsFreeShipping = grantsFreeShipping;
        PercentOff = percentOff;
        DiscountAmountMinor = discountAmountMinor;
        UpdatedAt = now;
    }

    /// <summary>
    /// Sets (or clears) the coupon code that gates this promotion (ADR-0052). Null/blank clears it and
    /// the promotion goes back to applying automatically. The code is normalized to trimmed UPPERCASE so
    /// storage, matching, the admin table and the shopper's entry all agree on one form; matching is
    /// still case-insensitive at checkout because a legacy row may carry any casing.
    /// <para>
    /// Uniqueness per tenant is a DATABASE guarantee (a filtered unique index on non-null codes) checked
    /// by the endpoint — the aggregate cannot see its siblings.
    /// </para>
    /// </summary>
    public void SetCode(string? code, DateTimeOffset now)
    {
        var normalized = NormalizeCode(code);
        // Dropping the code turns the promotion AUTOMATIC, and an automatic promotion with no threshold
        // would silently apply to every cart. Refuse rather than surprise the tenant with a store-wide sale.
        if (normalized is null && MinimumAmountMinor == 0 && MinimumQuantity == 0)
        {
            throw new CatalogRuleException(
                "Removing the coupon code makes this promotion automatic, so it needs a minimum amount or a minimum quantity.");
        }

        Code = normalized;
        UpdatedAt = now;
    }

    /// <summary>
    /// Normalizes a coupon code to its canonical form: trimmed, UPPERCASE, or null when blank. Shared with
    /// the endpoint's duplicate probe so the stored form and the probed form can never disagree.
    /// Codes are 1-40 characters of letters, digits, '-' or '_' — anything else is a typo the shopper can
    /// never type reliably (spaces, punctuation, smart quotes), so it is rejected rather than stored.
    /// </summary>
    public static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var normalized = code.Trim().ToUpperInvariant();
        if (normalized.Length > 40)
        {
            throw new CatalogRuleException("Coupon code must be 40 characters or fewer.");
        }

        foreach (var c in normalized)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                throw new CatalogRuleException("Coupon code may only contain letters, digits, '-' and '_'.");
            }
        }

        return normalized;
    }

    /// <summary>
    /// Sets the usage limits (ADR-0052): the total redemptions across all shoppers and the redemptions
    /// per customer. Null = unlimited on either axis; a set limit must be at least 1 (a zero limit is a
    /// promotion that can never apply — deactivate it instead, which is the honest expression of that).
    /// </summary>
    public void SetUsageLimits(int? maxRedemptions, int? maxRedemptionsPerCustomer, DateTimeOffset now)
    {
        if (maxRedemptions is { } max && max < 1)
        {
            throw new CatalogRuleException("Maximum redemptions must be at least 1 when set.");
        }

        if (maxRedemptionsPerCustomer is { } perCustomer && perCustomer < 1)
        {
            throw new CatalogRuleException("Maximum redemptions per customer must be at least 1 when set.");
        }

        MaxRedemptions = maxRedemptions;
        MaxRedemptionsPerCustomer = maxRedemptionsPerCustomer;
        UpdatedAt = now;
    }

    /// <summary>Sets combinability: true stacks with other combinable promotions, false is Exclusive.</summary>
    public void SetCombinable(bool combinable, DateTimeOffset now)
    {
        Combinable = combinable;
        UpdatedAt = now;
    }

    /// <summary>Scope this promotion to a single storefront (null = all storefronts of its currency).</summary>
    public void SetStorefront(Guid? storefrontId, DateTimeOffset now)
    {
        StorefrontId = storefrontId == Guid.Empty ? null : storefrontId;
        UpdatedAt = now;
    }

    /// <summary>Set the active window; either bound may be null (open-ended). From must not be after Until.</summary>
    public void SetActiveWindow(DateTimeOffset? activeFrom, DateTimeOffset? activeUntil, DateTimeOffset now)
    {
        if (activeFrom is { } from && activeUntil is { } until && from > until)
        {
            throw new CatalogRuleException("Promotion active-from must not be after active-until.");
        }

        ActiveFrom = activeFrom;
        ActiveUntil = activeUntil;
        UpdatedAt = now;
    }

    /// <summary>Rename the promotion (the shopper-visible label).</summary>
    public void Rename(string name, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CatalogRuleException("Promotion name is required.");
        }

        if (name.Trim().Length > 120)
        {
            throw new CatalogRuleException("Promotion name must be 120 characters or fewer.");
        }

        Name = name.Trim();
        UpdatedAt = now;
    }

    /// <summary>
    /// Whether this promotion can apply for <paramref name="storefrontId"/> at <paramref name="now"/>:
    /// Active, targeting that storefront (or all storefronts), and inside its inclusive
    /// [ActiveFrom, ActiveUntil] window (null bounds are open-ended). Currency matching is the caller's
    /// responsibility (a storefront's currency is 1:1).
    /// </summary>
    public bool IsEffectiveAt(DateTimeOffset now, Guid storefrontId) =>
        Status == PromotionStatus.Active
        && (StorefrontId is null || StorefrontId == storefrontId)
        && (ActiveFrom is null || ActiveFrom <= now)
        && (ActiveUntil is null || now <= ActiveUntil);

    /// <summary>Switch the promotion off; it stops applying immediately once the copy projects.</summary>
    public void Deactivate(DateTimeOffset now)
    {
        Status = PromotionStatus.Inactive;
        UpdatedAt = now;
    }

    /// <summary>Switch the promotion back on.</summary>
    public void Activate(DateTimeOffset now)
    {
        Status = PromotionStatus.Active;
        UpdatedAt = now;
    }
}
