"use client";

import { useActionState, useEffect, useMemo, useState } from "react";
import { useTranslations } from "next-intl";
import { addTicketMessage, type SupportState } from "@/lib/support-actions";
import type { OrderTicket } from "@/lib/gateway";
import { LocalTime } from "@/components/LocalTime";

// The customer's support-request history for this order: each ticket is its own chat thread with the
// full conversation, and an open ticket can be replied to. View-only for closed tickets.
export function TicketHistory({ orderId, tickets: initialTickets }: { orderId: string; tickets: OrderTicket[] }) {
  const t = useTranslations("support");
  const [tickets, setTickets] = useState(initialTickets);

  // Keep the live copy in sync when the server re-renders with fresh props (e.g. after the shopper posts).
  useEffect(() => setTickets(initialTickets), [initialTickets]);

  // Poll for new operator replies so they appear without a manual refresh — updates local state directly
  // (keyed by ticket id, so a half-typed reply is preserved). Only while the tab is visible.
  useEffect(() => {
    let alive = true;
    const poll = async () => {
      if (document.visibilityState !== "visible") return;
      try {
        const res = await fetch(`/api/order-tickets/${orderId}`, { cache: "no-store" });
        if (res.ok && alive) setTickets((await res.json()) as OrderTicket[]);
      } catch {
        /* transient — try again next tick */
      }
    };
    // Poll on an interval AND the moment the tab regains focus — so an operator reply posted while the
    // shopper was on another tab (e.g. the admin console) shows on return without waiting a full tick.
    const onVisible = () => {
      if (document.visibilityState === "visible") poll();
    };
    const id = setInterval(poll, 8000);
    document.addEventListener("visibilitychange", onVisible);
    return () => {
      alive = false;
      clearInterval(id);
      document.removeEventListener("visibilitychange", onVisible);
    };
  }, [orderId]);

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
  const lastActivity = useMemo(
    () => ticket.messages[ticket.messages.length - 1]?.createdAt ?? ticket.createdAt,
    [ticket],
  );
  // Collapsed by default — click the chevron to expand the thread. Open (still-active) requests start
  // expanded so a customer sees the running conversation without a click.
  const [expanded, setExpanded] = useState(open);
  return (
    <li className="rounded-md border border-neutral-100 bg-neutral-50">
      <button
        type="button"
        onClick={() => setExpanded((e) => !e)}
        aria-expanded={expanded}
        title={expanded ? t("collapse") : t("expand")}
        className="flex w-full items-center gap-2 px-3 py-2 text-left text-xs text-neutral-500"
      >
        <span aria-hidden className="text-neutral-400">{expanded ? "▾" : "▸"}</span>
        <span className="text-neutral-700">{t(reasonKey)}</span>
        <span className={`rounded-full px-2 py-0.5 font-medium ${open ? "bg-green-50 text-green-700" : "bg-neutral-100 text-neutral-500"}`}>
          {open ? t("statusOpen") : t("statusClosed")}
        </span>
        <span className="ml-auto text-neutral-400">
          {t("messageCount", { count: ticket.messages.length })} · <LocalTime iso={lastActivity} dateOnly />
        </span>
      </button>
      {expanded && (
      <div className="px-3 pb-3">
      <div className="space-y-2">
        {ticket.messages.map((m, i) => {
          const mine = m.author === "Customer";
          return (
            <div key={i} className={`max-w-[85%] rounded-lg border border-neutral-200 px-3 py-2 text-sm ${mine ? "ml-auto bg-white" : "bg-blue-50"}`}>
              <div className="mb-0.5 text-[0.7rem] text-neutral-500">
                {mine ? t("authorYou") : t("authorSupport")} · <LocalTime iso={m.createdAt} />
              </div>
              <div className="whitespace-pre-wrap">{m.body}</div>
            </div>
          );
        })}
      </div>
      {ticket.attachments?.length > 0 && (
        <div className="mt-2 flex flex-wrap gap-2">
          {ticket.attachments.map((a) => (
            <a
              key={a.id}
              href={`/api/support/attachment/${a.id}`}
              target="_blank"
              rel="noreferrer"
              className="inline-flex items-center gap-1 rounded border border-neutral-200 bg-white px-2 py-1 text-xs text-neutral-600 underline"
            >
              📎 {a.fileName}
            </a>
          ))}
        </div>
      )}
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
      </div>
      )}
    </li>
  );
}
