"use client";

import { useActionState, useEffect, useState } from "react";
import Link from "next/link";
import { convertGuest, type ConvertState } from "@/lib/cart-actions";

// Pending-first (components.md §2): show the saga state, then confirm via polling.
// In dev without a Stripe publishable key, a button completes a simulated test payment.
export function ConfirmationView({
  orderId,
  initialStatus,
  guestEmail,
  isAuthenticated,
}: {
  orderId: string;
  initialStatus: string;
  guestEmail: string;
  isAuthenticated: boolean;
}) {
  const [status, setStatus] = useState(initialStatus);
  const [paying, setPaying] = useState(false);
  const [convert, convertAction, converting] = useActionState<ConvertState, FormData>(convertGuest, {});

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
        {/* FR-7: guests can convert after checkout; authenticated orders are attached by Ordering.UserId. */}
        {!isAuthenticated && (
          <div className="rounded-md border border-neutral-200 p-4 text-left text-sm">
            {convert.ok ? (
            <p className="text-green-700">
              Account created — check your email to verify it, then your order appears in{" "}
              <Link href="/account" className="underline">your account</Link>.
            </p>
            ) : (
              <form action={convertAction} className="space-y-2">
              <p className="font-medium">Track your order — create an account</p>
              {convert.error && <p className="text-red-600">{convert.error}</p>}
              <input
                name="email"
                type="email"
                required
                defaultValue={guestEmail}
                placeholder="Email"
                className="w-full rounded border border-neutral-300 px-3 py-2"
              />
              <input
                name="password"
                type="password"
                required
                minLength={10}
                placeholder="Password (min 10 characters)"
                className="w-full rounded border border-neutral-300 px-3 py-2"
              />
              <button
                type="submit"
                disabled={converting}
                className="rounded-md bg-neutral-900 text-white px-4 py-2 text-sm disabled:opacity-50"
              >
                {converting ? "Creating…" : "Create account"}
              </button>
              </form>
            )}
          </div>
        )}
        {isAuthenticated && (
          <p className="rounded-md border border-green-200 bg-green-50 p-3 text-sm text-green-800">
            This order is attached to your account automatically.
          </p>
        )}
        {/* BL-5: make the order support / refund flow discoverable. */}
        <p className="text-sm text-neutral-500">
          Problem with this order?{" "}
          <Link href={`/orders/${orderId}/support`} className="underline">
            Contact support or request a refund
          </Link>
          .
        </p>
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
