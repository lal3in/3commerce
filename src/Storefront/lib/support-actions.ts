"use server";

import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { revalidatePath } from "next/cache";
import { GATEWAY_URL } from "./gateway";

async function authHeaders(): Promise<HeadersInit> {
  const store = await cookies();
  const session = store.get("3c_session");
  const headers: Record<string, string> = { "content-type": "application/json" };
  if (session) headers["cookie"] = `3c_session=${session.value}`;
  return headers;
}

export type SupportState = { error?: string; ok?: boolean };

async function cookieHeader(): Promise<Record<string, string>> {
  const store = await cookies();
  const session = store.get("3c_session");
  return session ? { cookie: `3c_session=${session.value}` } : {};
}

// Uploads an optional file attachment against a ticket ("tickets") or refund/return ("rma"). Best-effort:
// a failed upload doesn't undo the ticket/refund that was already created.
async function uploadAttachment(kind: "tickets" | "rma", ownerId: string, file: FormDataEntryValue | null): Promise<void> {
  if (!(file instanceof File) || file.size === 0) return;
  const body = new FormData();
  body.append("file", file);
  await fetch(`${GATEWAY_URL}/api/support/${kind}/${ownerId}/attachments`, {
    method: "POST",
    headers: await cookieHeader(),
    body,
  }).catch(() => {});
}

export async function openTicket(_prev: SupportState, formData: FormData): Promise<SupportState> {
  const orderId = String(formData.get("orderId"));
  const res = await fetch(`${GATEWAY_URL}/api/support/tickets`, {
    method: "POST",
    headers: await authHeaders(),
    body: JSON.stringify({
      orderId,
      email: String(formData.get("email")),
      reason: Number(formData.get("reason")),
      message: String(formData.get("message")),
    }),
  });
  if (!res.ok) return { error: "Could not open the ticket." };
  const ticket = (await res.json()) as { id: string };
  await uploadAttachment("tickets", ticket.id, formData.get("attachment"));
  revalidatePath(`/orders/${orderId}/support`);
  return { ok: true };
}

export async function addTicketMessage(_prev: SupportState, formData: FormData): Promise<SupportState> {
  const ticketId = String(formData.get("ticketId"));
  const body = String(formData.get("body") ?? "").trim();
  if (!body) return { error: "Write a message first." };
  const res = await fetch(`${GATEWAY_URL}/api/support/tickets/${ticketId}/messages`, {
    method: "POST",
    headers: await authHeaders(),
    body: JSON.stringify({ body }),
  });
  if (!res.ok) return { error: "Could not send your reply." };
  revalidatePath(`/orders/${String(formData.get("orderId"))}/support`);
  return { ok: true };
}

export async function requestRefund(_prev: SupportState, formData: FormData): Promise<SupportState> {
  const orderId = String(formData.get("orderId"));
  // Collect selected lines: form fields "line:<productId>" = quantity (server derives the amount).
  const lines: { productId: string; quantity: number }[] = [];
  for (const [key, value] of formData.entries()) {
    if (key.startsWith("line:")) {
      const qty = Number(value);
      if (qty > 0) lines.push({ productId: key.slice(5), quantity: qty });
    }
  }
  const res = await fetch(`${GATEWAY_URL}/api/support/rma`, {
    method: "POST",
    headers: await authHeaders(),
    body: JSON.stringify({ orderId, reason: String(formData.get("reason")), lines }),
  });
  if (!res.ok) return { error: "Could not submit the refund request." };
  const rma = (await res.json()) as { rmaId: string };
  await uploadAttachment("rma", rma.rmaId, formData.get("attachment"));
  redirect(`/orders/${orderId}/support?submitted=1`);
}
