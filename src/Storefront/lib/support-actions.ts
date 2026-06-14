"use server";

import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { GATEWAY_URL } from "./gateway";

async function authHeaders(): Promise<HeadersInit> {
  const store = await cookies();
  const session = store.get("3c_session");
  const headers: Record<string, string> = { "content-type": "application/json" };
  if (session) headers["cookie"] = `3c_session=${session.value}`;
  return headers;
}

export type SupportState = { error?: string; ok?: boolean };

export async function openTicket(_prev: SupportState, formData: FormData): Promise<SupportState> {
  const res = await fetch(`${GATEWAY_URL}/api/support/tickets`, {
    method: "POST",
    headers: await authHeaders(),
    body: JSON.stringify({
      orderId: String(formData.get("orderId")),
      email: String(formData.get("email")),
      reason: Number(formData.get("reason")),
      message: String(formData.get("message")),
    }),
  });
  return res.ok ? { ok: true } : { error: "Could not open the ticket." };
}

export async function requestRefund(_prev: SupportState, formData: FormData): Promise<SupportState> {
  const res = await fetch(`${GATEWAY_URL}/api/support/rma`, {
    method: "POST",
    headers: await authHeaders(),
    body: JSON.stringify({
      orderId: String(formData.get("orderId")),
      email: String(formData.get("email")),
      amountMinor: Number(formData.get("amountMinor")),
      reason: String(formData.get("reason")),
    }),
  });
  if (!res.ok) return { error: "Could not submit the refund request." };
  redirect(`/orders/${formData.get("orderId")}/support?submitted=1`);
}
