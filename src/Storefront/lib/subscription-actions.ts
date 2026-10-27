"use server";

import { cookies } from "next/headers";
import { revalidatePath } from "next/cache";
import { GATEWAY_URL } from "./gateway";
import type { PayRenewalState } from "./subscription-pay";

// The customer's own attempt to pay a past-due renewal (owner decision 2026-10-27, ADR-0057 amendment). Payments
// charges it ON-SESSION with a fresh attempt key and the period's pinned amount, optionally on another saved card.
// The Idempotency-Key is minted per page render (hidden field), so a double submit replays instead of paying twice.
export async function payRenewal(prev: PayRenewalState, formData: FormData): Promise<PayRenewalState> {
  const subscriptionId = String(formData.get("subscriptionId") ?? "");
  const idempotencyKey = String(formData.get("idempotencyKey") ?? "");
  const card = String(formData.get("savedPaymentMethodId") ?? "");
  const store = await cookies();
  const session = store.get("3c_session");
  const headers: Record<string, string> = { "content-type": "application/json", "Idempotency-Key": idempotencyKey };
  if (session) headers.cookie = `3c_session=${session.value}`;

  const response = await fetch(`${GATEWAY_URL}/api/payments/me/subscriptions/${encodeURIComponent(subscriptionId)}/pay`, {
    method: "POST",
    headers,
    body: JSON.stringify({ savedPaymentMethodId: card || null }),
    cache: "no-store",
  });
  revalidatePath(`/account/subscriptions/${subscriptionId}/pay`);
  const attempt = (prev.attempt ?? 0) + 1;
  if (response.status === 409) return { attempt, error: "notPastDue" };
  if (response.status === 422) return { attempt, error: "unknownCard" };
  if (!response.ok) return { attempt, error: "failed" };
  const body = (await response.json()) as { outcome: number; paymentIntentId: string | null };
  return { attempt, outcome: body.outcome, paymentIntentId: body.paymentIntentId };
}
