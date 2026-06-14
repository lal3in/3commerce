"use client";

import { useActionState } from "react";
import { openTicket, requestRefund, type SupportState } from "@/lib/support-actions";

// "Report a problem" + refund request (pending-first; components.md). Typed reasons per ADR-0018.
export function SupportForms({ orderId }: { orderId: string }) {
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
        <input name="email" type="email" required placeholder="Your email" className="w-full rounded border border-neutral-300 px-3 py-2 text-sm" />
        <input name="amountMinor" type="number" required placeholder="Amount in cents" className="w-full rounded border border-neutral-300 px-3 py-2 text-sm" />
        <input name="reason" required placeholder="Reason" className="w-full rounded border border-neutral-300 px-3 py-2 text-sm" />
        <button type="submit" disabled={rmaPending} className="rounded-md bg-neutral-900 text-white px-4 py-2 text-sm disabled:opacity-50">
          {rmaPending ? "Submitting…" : "Request refund"}
        </button>
      </form>
    </div>
  );
}
