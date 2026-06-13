"use client";

import { useEffect, useState } from "react";
import Link from "next/link";

// Pending-first (components.md §2): show the saga state, then confirm via polling.
// In dev without a Stripe publishable key, a button completes a simulated test payment.
export function ConfirmationView({ orderId, initialStatus }: { orderId: string; initialStatus: string }) {
  const [status, setStatus] = useState(initialStatus);
  const [paying, setPaying] = useState(false);

  useEffect(() => {
    if (status === "Confirmed" || status === "Cancelled") return;
    const timer = setInterval(async () => {
      const res = await fetch(`/api/order-status/${orderId}`, { cache: "no-store" });
      if (res.ok) {
        const data = (await res.json()) as { status: string };
        setStatus(data.status);
      }
    }, 2000);
    return () => clearInterval(timer);
  }, [orderId, status]);

  if (status === "Confirmed") {
    return (
      <div className="max-w-md mx-auto text-center py-12 space-y-4">
        <h1 className="text-2xl font-bold">Thank you!</h1>
        <p className="text-neutral-600">Your order is confirmed. A confirmation email is on its way.</p>
        <div className="rounded-md border border-neutral-200 p-4 text-left text-sm">
          <p className="font-medium">Track your order</p>
          <p className="text-neutral-500">Create a password to follow this order and reuse your details.</p>
          <Link href="/register" className="mt-2 inline-block underline">
            Set a password
          </Link>
        </div>
      </div>
    );
  }

  if (status === "Cancelled") {
    return (
      <div className="max-w-md mx-auto text-center py-12">
        <h1 className="text-xl font-semibold">Payment not completed</h1>
        <Link href="/cart" className="mt-4 inline-block underline">Return to cart</Link>
      </div>
    );
  }

  return (
    <div className="max-w-md mx-auto text-center py-12 space-y-4">
      <h1 className="text-xl font-semibold">Completing your payment…</h1>
      <p className="text-neutral-500 text-sm">Order {orderId}</p>
      <button
        type="button"
        disabled={paying}
        onClick={async () => {
          setPaying(true);
          await fetch(`/api/dev-pay/${orderId}`, { method: "POST" });
        }}
        className="rounded-md bg-neutral-900 text-white px-4 py-2 text-sm font-medium disabled:opacity-50"
      >
        {paying ? "Processing…" : "Complete test payment (dev)"}
      </button>
    </div>
  );
}
