// Money is always integer minor units + ISO 4217 code (AGENTS.md invariant).
// Never divide by 100 inline or hardcode a symbol — go through here (components.md §5).

// currency_3 / P3: the minor-unit exponent varies by currency and is NOT always 2.
// JPY has 0 decimals (1500 minor = ¥1,500), most currencies 2 (1500 = $15.00),
// KWD has 3 (1500 minor = KWD 1.500). Derive the exponent from Intl itself so there's
// no hardcoded table to drift, then divide by 10 ** exponent instead of a fixed 100.
function minorExponent(currency: string): number {
  try {
    // Intl already knows each currency's fraction digits; a bad/unknown code throws.
    return (
      new Intl.NumberFormat("en-US", { style: "currency", currency }).resolvedOptions()
        .maximumFractionDigits ?? 2
    );
  } catch {
    // Unknown/invalid currency code — fall back to the 2-decimal default so rendering never crashes.
    return 2;
  }
}

/** Convert integer minor units to a major-unit number for the currency (e.g. 1500 JPY → 1500, 1500 USD → 15). */
export function minorToMajor(minorUnits: number, currency: string): number {
  return minorUnits / 10 ** minorExponent(currency);
}

export function formatMoney(minorUnits: number, currency: string): string {
  // Explicit locale: deterministic server/client output (no hydration drift) and unambiguous
  // symbols — en-US renders AUD as "A$", USD as "$", EUR as "€" (rev_6 / F6). Intl also renders
  // the correct number of fraction digits per currency, so we only need the right divisor.
  try {
    return new Intl.NumberFormat("en-US", {
      style: "currency",
      currency,
    }).format(minorToMajor(minorUnits, currency));
  } catch {
    // Invalid currency code: fall back to a plain 2-decimal render so a bad code never crashes rendering.
    return `${currency} ${(minorUnits / 100).toFixed(2)}`;
  }
}
