/**
 * How settled the cart preview's promotion decision is (ADR-0054). Mirrors Ordering's PromotionBasis;
 * enums cross HTTP as NUMBERS (platform invariant), so these values are the wire contract.
 *
 * It lives in its own module — not in `gateway.ts` — because the CLIENT checkout form needs the values:
 * importing them from `gateway.ts` would drag `next/headers` into the browser bundle and fail the build.
 */
export const PromotionBasis = {
  /** The same promotions win at every shipping amount — the preview cannot contradict the charge. */
  Settled: 0,
  /** Scored against a known shipping amount (a real carrier quote, or a cart that pays no shipping). */
  Quoted: 1,
  /** The winner depends on a rate nobody knows yet: floor figures, free shipping undecided. */
  Provisional: 2,
} as const;
export type PromotionBasis = (typeof PromotionBasis)[keyof typeof PromotionBasis];
