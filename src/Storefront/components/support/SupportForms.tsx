"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { openTicket, requestRefund, type SupportState } from "@/lib/support-actions";
import type { RefundableOrder } from "@/lib/gateway";
import { formatMoney } from "@/lib/money";

// "Report a problem" + refund request (pending-first; components.md). Typed reasons per ADR-0018.
// The refund amount is derived server-side from the selected lines (BL-8) — never client input.
export function SupportForms({ orderId, refundable, email }: { orderId: string; refundable: RefundableOrder | null; email: string }) {
  const t = useTranslations("support");
  const [ticketState, ticketAction, ticketPending] = useActionState<SupportState, FormData>(openTicket, {});
  const [rmaState, rmaAction, rmaPending] = useActionState<SupportState, FormData>(requestRefund, {});

  return (
    <div className="space-y-8">
      <form action={ticketAction} className="space-y-3 rounded-md border border-neutral-200 p-4">
        <h2 className="font-medium">{t("reportProblem")}</h2>
        {ticketState.error && <p className="text-sm text-red-600">{ticketState.error}</p>}
        {ticketState.ok && <p className="text-sm text-green-700">{t("ticketOpened")}</p>}
        <input type="hidden" name="orderId" value={orderId} />
        {/* Email is the signed-in customer's verified address — pre-filled and read-only (they can't
            change who the ticket is from). Still submitted via the field so the server payload is unchanged. */}
        <label className="block text-sm text-neutral-600" title={t("tips.email")}>
          {t("yourEmail")}
          <input
            name="email"
            type="email"
            required
            readOnly
            value={email}
            aria-label={t("yourEmail")}
            title={t("tips.email")}
            aria-describedby="ticket-email-tip"
            className="mt-1 w-full rounded border border-neutral-200 bg-neutral-100 px-3 py-2 text-sm text-neutral-600"
          />
        </label>
        <span id="ticket-email-tip" className="sr-only">{t("tips.email")}</span>
        {/* Numeric on the wire (ADR-0018 SupportReason) — only the label is localized. */}
        <select
          name="reason"
          aria-label={t("reasons.whereIsMyOrder")}
          title={t("tips.reason")}
          aria-describedby="ticket-reason-tip"
          className="w-full rounded border border-neutral-300 px-3 py-2 text-sm"
        >
          <option value="1">{t("reasons.whereIsMyOrder")}</option>
          <option value="2">{t("reasons.damaged")}</option>
          <option value="3">{t("reasons.refund")}</option>
          <option value="4">{t("reasons.other")}</option>
        </select>
        <span id="ticket-reason-tip" className="sr-only">{t("tips.reason")}</span>
        <textarea
          name="message"
          required
          placeholder={t("describeIssue")}
          aria-label={t("describeIssue")}
          title={t("tips.message")}
          aria-describedby="ticket-message-tip"
          className="w-full rounded border border-neutral-300 px-3 py-2 text-sm"
          rows={3}
        />
        <span id="ticket-message-tip" className="sr-only">{t("tips.message")}</span>
        <label className="block text-sm text-neutral-600" title={t("attachHint")}>
          {t("attach")}
          <input name="attachment" type="file" accept="image/*,application/pdf" title={t("attachHint")} className="mt-1 block w-full text-sm" />
          <span className="text-xs text-neutral-400">{t("attachHint")}</span>
        </label>
        <button
          type="submit"
          disabled={ticketPending}
          title={t("tips.openTicket")}
          className="rounded-md bg-neutral-900 text-white px-4 py-2 text-sm disabled:opacity-50"
        >
          {ticketPending ? t("sending") : t("openTicket")}
        </button>
      </form>

      <form action={rmaAction} className="space-y-3 rounded-md border border-neutral-200 p-4">
        <h2 className="font-medium">{t("requestRefund")}</h2>
        {rmaState.error && <p className="text-sm text-red-600">{rmaState.error}</p>}
        <input type="hidden" name="orderId" value={orderId} />
        {refundable === null ? (
          <p className="text-sm text-neutral-500">{t("notRefundable")}</p>
        ) : (
          <>
            <p className="text-sm text-neutral-500">{t("selectItems")}</p>
            <ul className="space-y-1">
              {refundable.lines.map((line) => (
                <li key={line.productId} className="flex items-center gap-2 text-sm">
                  <input
                    type="number"
                    name={`line:${line.productId}`}
                    min={0}
                    max={line.quantity}
                    defaultValue={0}
                    aria-label={t("refundQuantityFor", { title: line.title })}
                    title={t("tips.refundQuantity")}
                    className="w-16 rounded border border-neutral-300 px-2 py-1"
                  />
                  <span className="flex-1">{line.title}</span>
                  <span className="text-neutral-500">
                    {t("lineSummary", {
                      price: formatMoney(line.unitPriceMinor, refundable.currency),
                      quantity: line.quantity,
                    })}
                  </span>
                </li>
              ))}
            </ul>
            <input
              name="reason"
              required
              placeholder={t("reason")}
              aria-label={t("reason")}
              title={t("tips.refundReason")}
              aria-describedby="rma-reason-tip"
              className="w-full rounded border border-neutral-300 px-3 py-2 text-sm"
            />
            <span id="rma-reason-tip" className="sr-only">{t("tips.refundReason")}</span>
            <label className="block text-sm text-neutral-600" title={t("attachHint")}>
              {t("attach")}
              <input name="attachment" type="file" accept="image/*,application/pdf" title={t("attachHint")} className="mt-1 block w-full text-sm" />
              <span className="text-xs text-neutral-400">{t("attachHint")}</span>
            </label>
            <button
              type="submit"
              disabled={rmaPending}
              title={t("tips.submitRefund")}
              className="rounded-md bg-neutral-900 text-white px-4 py-2 text-sm disabled:opacity-50"
            >
              {rmaPending ? t("submitting") : t("submitRefund")}
            </button>
          </>
        )}
      </form>
    </div>
  );
}
