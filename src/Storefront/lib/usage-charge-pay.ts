// Client-safe values for the usage "complete payment" page (usage_charge_pay_page). A "use server" module may export
// only async functions, so the outcome numbers and the state type live here.

// Mirrors Payments' UsageChargePayOutcome — crosses HTTP as a NUMBER (platform invariant). Same values as the
// subscription pay page's PayOutcome.
export const UsagePayOutcome = {
  Paid: 1,
  RequiresAction: 2,
  Declined: 3,
  ProviderUnavailable: 4,
  NotPayable: 5,
  UnknownPaymentMethod: 6,
} as const;

// Mirrors Payments' UsageChargeKind (numbers).
export const UsageChargeKind = { Overage: 1, AutoLoad: 2 } as const;

// Mirrors BuildingBlocks MeterType (numbers) → the message key of its name.
export const MeterKeys: Record<number, "Token" | "Transaction" | "Request" | "Minute" | "Seat" | "StorageGb"> = {
  1: "Token",
  2: "Transaction",
  3: "Request",
  4: "Minute",
  5: "Seat",
  6: "StorageGb",
};

export type PayUsageChargeState = {
  attempt?: number; // submissions answered so far — the next submission's Idempotency-Key suffix
  outcome?: number;
  paymentIntentId?: string | null;
  error?: "failed" | "notPayable" | "unknownCard";
};
