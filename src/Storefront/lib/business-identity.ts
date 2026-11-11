// Client-safe values for the account's default business identity (ADR-0063 P3b) — a "use server" module may export
// only async functions, so the form's state type lives here.

/** The member's saved default business identity, as Identity returns it (normalized; the scheme is a NUMBER). */
export type BusinessIdentityDto = {
  companyName: string;
  taxId: string | null;
  taxIdScheme: number | null;
  taxIdJurisdiction: string | null;
};

/**
 * The account form's state. `field` is the input Identity refused (problem+json `field`: companyName / taxId /
 * taxIdScheme / taxIdJurisdiction), which `businessFieldErrorKey` turns into a `checkout.business.errors.*` message.
 * `values` echoes what was submitted so a refusal keeps what the member typed.
 */
export type BusinessIdentityState = {
  status: "idle" | "saved" | "cleared" | "error";
  field?: string | null;
  values?: { companyName: string; taxId: string; taxIdScheme: string; taxIdJurisdiction: string };
};
