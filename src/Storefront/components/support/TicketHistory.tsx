"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { addTicketMessage, type SupportState } from "@/lib/support-actions";
import type { OrderTicket } from "@/lib/gateway";

// The customer's support-request history for this order: each ticket is its own chat thread with the
// full conversation, and an open ticket can be replied to. View-only for closed tickets.
export function TicketHistory({ orderId, tickets }: { orderId: string; tickets: OrderTicket[] }) {
  const t = useTranslations("support");
  if (tickets.length === 0) return null;
  return (
    <section className="space-y-3 rounded-md border border-neutral-200 p-4">
      <h2 className="font-medium">{t("history.title")}</h2>
      <ul className="space-y-4">
        {tickets.map((ticket) => (
          <TicketThread key={ticket.id} orderId={orderId} ticket={ticket} />
        ))}
      </ul>
    </section>
  );
}

function TicketThread({ orderId, ticket }: { orderId: string; ticket: OrderTicket }) {
  const t = useTranslations("support");
  const [state, action, pending] = useActionState<SupportState, FormData>(addTicketMessage, {});
  const open = ticket.status === "Open";
  const reasonKey =
    ticket.reason === "WhereIsIt" ? "reasons.whereIsMyOrder"
    : ticket.reason === "Damaged" ? "reasons.damaged"
    : ticket.reason === "RefundRequest" ? "reasons.refund"
    : "reasons.other";
  return (
    <li className="rounded-md border border-neutral-100 bg-neutral-50 p-3">
      <div className="mb-2 flex items-center justify-between text-xs text-neutral-500">
        <span>{t(reasonKey)}</span>
        <span className={open ? "text-green-700" : "text-neutral-500"}>
          {open ? t("statusOpen") : t("statusClosed")}
        </span>
      </div>
      <div className="space-y-2">
        {ticket.messages.map((m, i) => {
          const mine = m.author === "Customer";
          return (
            <div key={i} className={`max-w-[85%] rounded-lg border border-neutral-200 px-3 py-2 text-sm ${mine ? "ml-auto bg-white" : "bg-blue-50"}`}>
              <div className="mb-0.5 text-[0.7rem] text-neutral-500">
                {mine ? t("authorYou") : t("authorSupport")} · {new Date(m.createdAt).toLocaleString()}
              </div>
              <div className="whitespace-pre-wrap">{m.body}</div>
            </div>
          );
        })}
      </div>
      {open ? (
        <form action={action} className="mt-2 flex items-start gap-2">
          <input type="hidden" name="ticketId" value={ticket.id} />
          <input type="hidden" name="orderId" value={orderId} />
          <textarea
            name="body"
            required
            rows={2}
            placeholder={t("replyPlaceholder")}
            aria-label={t("replyPlaceholder")}
            className="flex-1 rounded border border-neutral-300 px-3 py-2 text-sm"
          />
          <button
            type="submit"
            disabled={pending}
            title={t("tips.sendReply")}
            className="rounded-md bg-neutral-900 px-4 py-2 text-sm text-white disabled:opacity-50"
          >
            {pending ? t("sending") : t("sendReply")}
          </button>
        </form>
      ) : (
        <p className="mt-2 text-xs text-neutral-500">{t("threadClosed")}</p>
      )}
      {state.error && <p className="mt-1 text-sm text-red-600">{state.error}</p>}
    </li>
  );
}
