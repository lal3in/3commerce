"use server";

import { cookies } from "next/headers";
import { GATEWAY_URL } from "./gateway";
import type { PayUsageChargeState } from "./usage-charge-pay";

// The customer's own attempt to pay an unpaid usage charge (usage_charge_pay_page). Payments charges it ON-SESSION with a
// fresh attempt key and the charge's pinned amount, optionally on another saved card. The Idempotency-Key is minted per
// page render (hidden field), so a double submit replays instead of paying twice.
export async function payUsageCharge(prev: PayUsageChargeState, formData: FormData): Promise<PayUsageChargeState> {
  const reference = String(formData.get("reference") ?? "");
  const idempotencyKey = String(formData.get("idempotencyKey") ?? "");
  const card = String(formData.get("savedPaymentMethodId") ?? "");
  const store = await cookies();
  const session = store.get("3c_session");
  const headers: Record<string, string> = { "content-type": "application/json", "Idempotency-Key": idempotencyKey };
  if (session) headers.cookie = `3c_session=${session.value}`;

  const response = await fetch(`${GATEWAY_URL}/api/payments/me/usage-charges/${encodeURIComponent(reference)}/pay`, {
    method: "POST",
    headers,
    body: JSON.stringify({ savedPaymentMethodId: card || null }),
    cache: "no-store",
  });
  // No revalidatePath (the #315 lesson): re-rendering this page after a successful payment finds the charge Settled and
  // unmounts the form — and with it the "paid" confirmation. The page and /account are dynamic (cookie-dependent, never
  // cached), so the next visit reads the new state anyway.
  const attempt = (prev.attempt ?? 0) + 1;
  if (response.status === 409) return { attempt, error: "notPayable" };
  if (response.status === 422) return { attempt, error: "unknownCard" };
  if (!response.ok) return { attempt, error: "failed" };
  const body = (await response.json()) as { outcome: number; paymentIntentId: string | null };
  return { attempt, outcome: body.outcome, paymentIntentId: body.paymentIntentId };
}
