// Client-safe values for the renewal "complete payment" page (a "use server" module may export only async functions).

// Mirrors Payments' SubscriptionPayOutcome — crosses HTTP as a NUMBER (platform invariant).
export const PayOutcome = {
  Paid: 1,
  RequiresAction: 2,
  Declined: 3,
  ProviderUnavailable: 4,
  NotPastDue: 5,
  UnknownPaymentMethod: 6,
} as const;

export type PayRenewalState = {
  attempt?: number; // submissions answered so far — the next submission's Idempotency-Key suffix
  outcome?: number;
  paymentIntentId?: string | null;
  error?: "failed" | "notPastDue" | "unknownCard";
};
