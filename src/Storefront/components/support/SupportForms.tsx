"use client";

import { useActionState } from "react";
import { openTicket, requestRefund, type SupportState } from "@/lib/support-actions";
import type { RefundableOrder } from "@/lib/gateway";
import { formatMoney } from "@/lib/money";

// "Report a problem" + refund request (pending-first; components.md). Typed reasons per ADR-0018.
// The refund amount is derived server-side from the selected lines (BL-8) — never client input.
export function SupportForms({ orderId, refundable }: { orderId: string; refundable: RefundableOrder | null }) {
  const [ticketState, ticketAction, ticketPending] = useActionState<SupportState, FormData>(openTicket, {});
  const [rmaState, rmaAction, rmaPending] = useActionState<SupportState, FormData>(requestRefund, {});

  return (
    <div className="space-y-8">
      <form action={ticketAction} className="space-y-3 rounded-md border border-neutral-200 p-4">
        <h2 className="font-medium">Report a problem</h2>
        {ticketState.error && <p className="text-sm text-red-600">{ticketState.error}</p>}
        {ticketState.ok && <p className="text-sm text-green-700">Ticket opened.</p>}
        <input type="hidden" name="orderId" value={orderId} />
        <input name="email" type="email" required placeholder="Your email" className="w-full rounded border border-neutral-300 px-3 py-2 text-sm" />
        <select name="reason" className="w-full rounded border border-neutral-300 px-3 py-2 text-sm">
          <option value="1">Where is my order?</option>
          <option value="2">Arrived damaged</option>
          <option value="3">Refund request</option>
          <option value="4">Other</option>
        </select>
        <textarea name="message" required placeholder="Describe the issue" className="w-full rounded border border-neutral-300 px-3 py-2 text-sm" rows={3} />
        <button type="submit" disabled={ticketPending} className="rounded-md bg-neutral-900 text-white px-4 py-2 text-sm disabled:opacity-50">
          {ticketPending ? "Sending…" : "Open ticket"}
        </button>
      </form>

      <form action={rmaAction} className="space-y-3 rounded-md border border-neutral-200 p-4">
        <h2 className="font-medium">Request a refund</h2>
        {rmaState.error && <p className="text-sm text-red-600">{rmaState.error}</p>}
        <input type="hidden" name="orderId" value={orderId} />
        {refundable === null ? (
          <p className="text-sm text-neutral-500">This order isn&apos;t available for a refund request.</p>
        ) : (
          <>
            <p className="text-sm text-neutral-500">Select the items to refund (leave all unchecked to refund the whole order):</p>
            <ul className="space-y-1">
              {refundable.lines.map((l) => (
                <li key={l.productId} className="flex items-center gap-2 text-sm">
                  <input
                    type="number"
                    name={`line:${l.productId}`}
                    min={0}
                    max={l.quantity}
                    defaultValue={0}
                    aria-label={`Quantity to refund for ${l.title}`}
                    className="w-16 rounded border border-neutral-300 px-2 py-1"
                  />
                  <span className="flex-1">{l.title}</span>
                  <span className="text-neutral-500">
                    {formatMoney(l.unitPriceMinor, refundable.currency)} × {l.quantity}
                  </span>
                </li>
              ))}
            </ul>
            <input name="reason" required placeholder="Reason" className="w-full rounded border border-neutral-300 px-3 py-2 text-sm" />
            <button type="submit" disabled={rmaPending} className="rounded-md bg-neutral-900 text-white px-4 py-2 text-sm disabled:opacity-50">
              {rmaPending ? "Submitting…" : "Request refund"}
            </button>
          </>
        )}
      </form>
    </div>
  );
}
