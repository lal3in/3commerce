namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// The tax category of one line (ADR-0063 § 7.1, P1). Persisted and — from P5 — published, so it crosses the
/// wire as a NUMBER: append members, never renumber. The members mirror the EN 16931 / UNCL5305 VAT category
/// codes an invoice will print (<see cref="Standard"/> = <c>S</c>, <see cref="Exempt"/> = <c>E</c>); the
/// OSS-aware strategy (P1b) appends the reduced / zero-rated categories it needs.
/// </summary>
public enum TaxCategory
{
    /// <summary>No tax facts were recorded: a row written before per-line tax existed (ADR-0063 D18 — no backfill).</summary>
    NotRecorded = 0,

    /// <summary>Taxed at the strategy's rate (which may be 0 % on a storefront that charges no tax).</summary>
    Standard = 1,

    /// <summary>Not taxed — <see cref="LineTax.ExemptionReason"/> says why.</summary>
    Exempt = 2,
}

/// <summary>
/// Why a line (or the shipping line) carries no tax although the storefront charges a rate. Crosses the wire as a
/// NUMBER: append members, never renumber.
/// </summary>
public enum TaxExemptionReason
{
    None = 0,

    /// <summary>The product's ship rule for the ship-to country says <c>ChargeDestinationTax = false</c> (ADR-0050).</summary>
    DestinationShipRule = 1,

    /// <summary>
    /// Shipping follows the goods' taxability: no line of the order is destination-taxable, so its shipping is not
    /// destination-taxed either (checkout's long-standing rule).
    /// </summary>
    ShippingFollowsExemptGoods = 2,

    /// <summary>The product's own <see cref="TaxMode.Exempt"/> (the pricing engine's vocabulary; checkout never sets it).</summary>
    ProductTaxMode = 3,
}

/// <summary>
/// The tax scheme the sale falls under (ADR-0063 § 7.1). Every strategy that exists today charges its storefront's
/// own single domestic rate, so it is always <see cref="Domestic"/>; the EU OSS-aware strategy (P1b) and the B2B
/// work (phase 2) produce the others. Crosses the wire as a NUMBER: append members, never renumber.
/// </summary>
public enum TaxScheme
{
    /// <summary>No tax facts were recorded (an order placed before per-line tax existed).</summary>
    NotRecorded = 0,
    Domestic = 1,
    Oss = 2,
    Ioss = 3,
    ReverseCharge = 4,
    IntraEuExempt = 5,
    Export = 6,
    OutOfScope = 7,
}

/// <summary>One order line as the tax strategy sees it.</summary>
/// <param name="AmountMinor">The line's listed value: unit price × quantity, before any discount.</param>
/// <param name="DiscountMinor">
/// The line's share of the order discount — its promotion allocation plus its share of the storefront-wide
/// discount, exactly as checkout splits them (both largest remainder, ADR-0051/0053).
/// </param>
/// <param name="Exemption">Why the line is not taxable, or <see cref="TaxExemptionReason.None"/> when it is.</param>
public sealed record TaxLineInput(long AmountMinor, long DiscountMinor, TaxExemptionReason Exemption = TaxExemptionReason.None);

/// <summary>What a tax strategy is asked to tax. Every amount is in minor units of <see cref="Currency"/>.</summary>
/// <param name="ShippingMinor">The shipping actually charged (after free-shipping promotions and waivers).</param>
public sealed record TaxCalculationInput(
    string Currency,
    string? ShipCountry,
    IReadOnlyList<TaxLineInput> Lines,
    long ShippingMinor = 0);

/// <summary>
/// The tax facts of one line or of the shipping line (ADR-0063 § 7.1).
/// </summary>
/// <param name="RateBps">The rate applied, in basis points (0 for an exempt line).</param>
/// <param name="TaxableBaseMinor">
/// The amount the tax is levied on, ALWAYS NET of this tax: on an exclusive storefront the discounted amount the
/// tax was added to; on an inclusive one the discounted shelf amount less the tax it contains. So
/// <c>base + tax</c> is what the shopper paid for the component on an inclusive storefront, and <c>base</c> is
/// what they paid before tax on an exclusive one. For an exempt line it is the line's discounted amount.
/// </param>
/// <param name="TaxMinor">This component's share of the order's tax (largest remainder — the shares sum to the total).</param>
public sealed record LineTax(
    TaxCategory Category,
    int RateBps,
    long TaxableBaseMinor,
    long TaxMinor,
    TaxExemptionReason ExemptionReason = TaxExemptionReason.None);

/// <summary>One row of the per-rate summary: Σ of the components taxed at that category and rate.</summary>
public sealed record TaxRateSummary(TaxCategory Category, int RateBps, long TaxableBaseMinor, long TaxMinor);

/// <summary>
/// A strategy's answer (ADR-0063 § 7.1): the tax of every line (index-aligned with
/// <see cref="TaxCalculationInput.Lines"/>) and of the shipping line, the scheme, and whether the tax is
/// contained in the prices (inclusive) or added to them (exclusive). The order's tax IS the sum of the parts, so
/// <c>Σ line tax + shipping tax == TaxMinor</c> holds by construction.
/// </summary>
public sealed record TaxBreakdown(
    IReadOnlyList<LineTax> Lines,
    LineTax Shipping,
    TaxScheme Scheme,
    bool TaxInclusive)
{
    /// <summary>The order's whole tax — Σ line tax + shipping tax.</summary>
    public long TaxMinor => Lines.Sum(l => l.TaxMinor) + Shipping.TaxMinor;

    /// <summary>
    /// The per-rate summary (Art. 226(8)–(10)): Σ of the lines — and of the shipping line when shipping was charged —
    /// grouped by category and rate, in first-seen order. Never recomputed as <c>base × rate</c>, which could drift a
    /// minor unit from what was charged (ADR-0063 § 8).
    /// </summary>
    public IReadOnlyList<TaxRateSummary> RateSummary
    {
        get
        {
            var components = Shipping.TaxableBaseMinor != 0 || Shipping.TaxMinor != 0 ? Lines.Append(Shipping) : Lines;
            return components
                .GroupBy(c => (c.Category, c.RateBps))
                .Select(g => new TaxRateSummary(g.Key.Category, g.Key.RateBps, g.Sum(c => c.TaxableBaseMinor), g.Sum(c => c.TaxMinor)))
                .ToList();
        }
    }
}

/// <summary>
/// THE tax seam (ADR-0063 § 7.1): what tax an order is charged, broken down per line, per shipping line and per
/// rate. Checkout calls it once and persists the facts; it never computes tax inline.
/// </summary>
public interface ITaxStrategy
{
    public TaxBreakdown TaxFor(TaxCalculationInput input);
}

/// <summary>
/// The storefront's single rate in its own regime (ADR-0038/0055) — the logic checkout used to compute inline,
/// moved here unchanged (ADR-0063 P1, behaviour-preserving):
/// <list type="bullet">
/// <item>the goods base is the destination-taxable lines less their discount (promotion + storefront-wide share),
/// clamped at their subtotal; shipping joins the base only when some goods are taxable;</item>
/// <item>inclusive (AU GST / EU VAT): the tax CONTAINED in the base, <c>base × bps / (10000 + bps)</c>;
/// exclusive (US sales tax): the tax ADDED to it, <c>base × bps / 10000</c> — both rounded once, on the whole base,
/// half away from zero (the same arithmetic as Payments' <c>RecurringChargeTax</c>);</item>
/// <item>that one total is then split over the taxable lines and the shipping line by largest remainder, weighted
/// by each component's share of the base, so the parts sum to it exactly.</item>
/// </list>
/// Rounding the total once (not per line) is what keeps the charged gross byte-identical to before.
/// </summary>
public sealed class StorefrontRateTaxStrategy(int rateBasisPoints, bool taxInclusive) : ITaxStrategy
{
    /// <summary>
    /// The strategy for THIS storefront's projected config (ADR-0055: by storefront, never by currency). No projected
    /// copy = the historical no-tax fallback (0 %, exclusive).
    /// </summary>
    public static StorefrontRateTaxStrategy For(StorefrontTaxCopy? storefront) =>
        new(storefront?.TaxRateBasisPoints ?? 0, storefront?.TaxInclusive == true);

    public int RateBasisPoints => rateBasisPoints;

    public bool TaxInclusive => taxInclusive;

    public TaxBreakdown TaxFor(TaxCalculationInput input)
    {
        long taxableSubtotal = 0, taxableDiscount = 0;
        foreach (var line in input.Lines)
        {
            if (line.Exemption == TaxExemptionReason.None)
            {
                taxableSubtotal += line.AmountMinor;
                taxableDiscount += line.DiscountMinor;
            }
        }

        var goodsBase = taxableSubtotal - Math.Min(taxableDiscount, taxableSubtotal);
        var taxableShipping = taxableSubtotal > 0 ? input.ShippingMinor : 0;
        var taxBase = goodsBase + taxableShipping;
        var tax = taxInclusive ? InclusiveTaxOn(taxBase, rateBasisPoints) : ExclusiveTaxOn(taxBase, rateBasisPoints);
        return TaxAllocation.Build(
            input.Lines, goodsBase, input.ShippingMinor, taxableShipping, tax, rateBasisPoints, taxInclusive, TaxScheme.Domestic);
    }

    /// <summary>The tax CONTAINED in a tax-inclusive amount: <c>amount × bps / (10000 + bps)</c>, half away from zero.</summary>
    public static long InclusiveTaxOn(long amountMinor, int rateBasisPoints) =>
        amountMinor <= 0 || rateBasisPoints <= 0
            ? 0
            : (long)Math.Round(amountMinor * (decimal)rateBasisPoints / (10000m + rateBasisPoints), MidpointRounding.AwayFromZero);

    /// <summary>The tax ADDED to a tax-exclusive amount: <c>amount × bps / 10000</c>, half away from zero.</summary>
    public static long ExclusiveTaxOn(long amountMinor, int rateBasisPoints) =>
        amountMinor <= 0 || rateBasisPoints <= 0
            ? 0
            : (long)Math.Round(amountMinor * (decimal)rateBasisPoints / 10000m, MidpointRounding.AwayFromZero);
}

/// <summary>No tax at all (the pricing engine's default).</summary>
public sealed class ZeroTaxStrategy : ITaxStrategy
{
    public static ZeroTaxStrategy Instance { get; } = new();

    private ZeroTaxStrategy()
    {
    }

    public TaxBreakdown TaxFor(TaxCalculationInput input) =>
        TaxAllocation.Build(input.Lines, goodsBaseMinor: null, input.ShippingMinor, 0, 0, 0, taxInclusive: false, TaxScheme.Domestic);
}

/// <summary>
/// The pricing engine's home-country strategy: one rate, only when shipping to the home country, on the goods
/// alone (never shipping), the discount apportioned to the taxable goods by value (integer division) and the tax
/// rounded half-to-even. Kept exactly as it was; checkout does not use it.
/// </summary>
public sealed class HomeRegimeTaxStrategy(string? homeCountry, int rateBasisPoints, bool pricesIncludeTax = false) : ITaxStrategy
{
    public TaxBreakdown TaxFor(TaxCalculationInput input)
    {
        if (string.IsNullOrWhiteSpace(homeCountry) || rateBasisPoints <= 0
            || !string.Equals(input.ShipCountry, homeCountry, StringComparison.OrdinalIgnoreCase))
        {
            return TaxAllocation.Build(input.Lines, goodsBaseMinor: null, 0, 0, 0, 0, pricesIncludeTax, TaxScheme.Domestic);
        }

        var taxableSubtotal = input.Lines
            .Where(l => l.Exemption == TaxExemptionReason.None)
            .Sum(l => l.AmountMinor);
        var totalSubtotal = input.Lines.Sum(l => l.AmountMinor);
        if (taxableSubtotal == 0 || totalSubtotal == 0)
        {
            return TaxAllocation.Build(input.Lines, goodsBaseMinor: null, 0, 0, 0, rateBasisPoints, pricesIncludeTax, TaxScheme.Domestic);
        }

        var discountMinor = input.Lines.Sum(l => l.DiscountMinor);
        var taxableDiscount = discountMinor * taxableSubtotal / totalSubtotal;
        var taxableBase = Math.Max(0, taxableSubtotal - taxableDiscount);
        var tax = pricesIncludeTax
            ? (long)Math.Round(taxableBase * rateBasisPoints / (10000m + rateBasisPoints), MidpointRounding.ToEven)
            : (long)Math.Round(taxableBase * rateBasisPoints / 10000m, MidpointRounding.ToEven);
        return TaxAllocation.Build(input.Lines, taxableBase, 0, 0, tax, rateBasisPoints, pricesIncludeTax, TaxScheme.Domestic);
    }
}

/// <summary>
/// Turns a strategy's aggregate decision (goods base, taxable shipping, ONE rounded total) into per-component facts
/// whose tax sums to that total exactly — largest remainder, the same rule as the discount allocations
/// (ADR-0051/0055), so no minor unit is lost or invented between the lines and <c>Order.TaxMinor</c>.
/// </summary>
public static class TaxAllocation
{
    /// <param name="lines">The strategy's input lines.</param>
    /// <param name="goodsBaseMinor">
    /// The aggregate taxable goods base the strategy taxed, or null for "each taxable line's own discounted amount".
    /// </param>
    /// <param name="shippingMinor">The shipping charged.</param>
    /// <param name="taxableShippingMinor">The part of it in the tax base (0 or <paramref name="shippingMinor"/>).</param>
    /// <param name="taxMinor">The ONE rounded total the strategy charged.</param>
    /// <param name="rateBps">The rate applied to the taxable components.</param>
    /// <param name="taxInclusive">Whether the base CONTAINS the tax (its net base is then base − tax).</param>
    /// <param name="scheme">The sale's scheme.</param>
    public static TaxBreakdown Build(
        IReadOnlyList<TaxLineInput> lines,
        long? goodsBaseMinor,
        long shippingMinor,
        long taxableShippingMinor,
        long taxMinor,
        int rateBps,
        bool taxInclusive,
        TaxScheme scheme)
    {
        var count = lines.Count;

        // 1. Each taxable line's share of the goods base. Normally that is simply its discounted amount; but the
        // strategy clamps the discount on the WHOLE taxable subtotal, so where one line was discounted past its own
        // value the excess comes off the others — spread the aggregate over the lines' own amounts then.
        var ownBase = new long[count];
        long ownBaseSum = 0;
        for (var i = 0; i < count; i++)
        {
            if (lines[i].Exemption == TaxExemptionReason.None)
            {
                ownBase[i] = Math.Max(0, lines[i].AmountMinor - lines[i].DiscountMinor);
                ownBaseSum += ownBase[i];
            }
        }

        var goodsBase = goodsBaseMinor ?? ownBaseSum;
        long[] lineBase;
        if (goodsBase == ownBaseSum)
        {
            lineBase = ownBase;
        }
        else
        {
            var weights = ownBaseSum > 0
                ? ownBase
                : lines.Select(l => l.Exemption == TaxExemptionReason.None ? Math.Max(0, l.AmountMinor) : 0).ToArray();
            lineBase = Allocate(goodsBase, weights);
        }

        // 2. The ONE total over [lines…, shipping], weighted by each component's part of the base.
        var weightsWithShipping = new long[count + 1];
        Array.Copy(lineBase, weightsWithShipping, count);
        weightsWithShipping[count] = taxableShippingMinor;
        var taxParts = Allocate(taxMinor, weightsWithShipping);

        var facts = new LineTax[count];
        for (var i = 0; i < count; i++)
        {
            facts[i] = lines[i].Exemption == TaxExemptionReason.None
                ? new LineTax(TaxCategory.Standard, rateBps, Net(lineBase[i], taxParts[i], taxInclusive), taxParts[i])
                : new LineTax(TaxCategory.Exempt, 0, Math.Max(0, lines[i].AmountMinor - lines[i].DiscountMinor), 0, lines[i].Exemption);
        }

        var shipping = shippingMinor > 0 && taxableShippingMinor == 0
            ? new LineTax(TaxCategory.Exempt, 0, shippingMinor, 0, TaxExemptionReason.ShippingFollowsExemptGoods)
            : new LineTax(TaxCategory.Standard, rateBps, Net(taxableShippingMinor, taxParts[count], taxInclusive), taxParts[count]);

        return new TaxBreakdown(facts, shipping, scheme, taxInclusive);
    }

    private static long Net(long baseMinor, long taxMinor, bool taxInclusive) => taxInclusive ? baseMinor - taxMinor : baseMinor;

    /// <summary>
    /// Largest remainder over <paramref name="weights"/>; ties go to the earliest component. A positive amount with no
    /// weight to sit on cannot come out of a strategy (tax &gt; 0 needs a base &gt; 0); it is refused loudly rather than
    /// dropped, because a dropped minor unit would silently change the charge.
    /// </summary>
    private static long[] Allocate(long amount, IReadOnlyList<long> weights)
    {
        if (amount <= 0)
        {
            return new long[weights.Count];
        }

        if (weights.Sum() <= 0)
        {
            throw new InvalidOperationException($"Cannot allocate {amount} minor units of tax over a zero base.");
        }

        return PromotionEvaluator.AllocateProportionally(amount, weights);
    }
}
