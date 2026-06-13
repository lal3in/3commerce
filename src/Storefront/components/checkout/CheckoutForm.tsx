"use client";

import { useActionState } from "react";
import { submitCheckout, type CheckoutState } from "@/lib/cart-actions";

// Guest checkout: email + shipping only (ADR-0013). Server action calls the gateway and
// redirects to the confirmation page (pending-first per components.md §2).
export function CheckoutForm() {
  const [state, action, pending] = useActionState<CheckoutState, FormData>(submitCheckout, {});

  return (
    <form action={action} className="space-y-3">
      {state.error && (
        <p role="alert" className="rounded bg-red-50 text-red-700 px-3 py-2 text-sm">
          {state.error}
        </p>
      )}
      <Field name="email" label="Email" type="email" autoComplete="email" />
      <Field name="name" label="Full name" autoComplete="name" />
      <Field name="line1" label="Address" autoComplete="address-line1" />
      <div className="grid grid-cols-2 gap-3">
        <Field name="city" label="City" autoComplete="address-level2" />
        <Field name="postcode" label="Postcode" autoComplete="postal-code" />
      </div>
      <Field name="country" label="Country (2-letter)" autoComplete="country" maxLength={2} />
      <button
        type="submit"
        disabled={pending}
        className="w-full rounded-md bg-neutral-900 text-white py-3 text-sm font-medium disabled:opacity-50"
      >
        {pending ? "Placing order…" : "Place order"}
      </button>
    </form>
  );
}

function Field({ name, label, type = "text", autoComplete, maxLength }: {
  name: string; label: string; type?: string; autoComplete?: string; maxLength?: number;
}) {
  return (
    <div>
      <label htmlFor={name} className="block text-sm font-medium">{label}</label>
      <input
        id={name}
        name={name}
        type={type}
        required
        autoComplete={autoComplete}
        maxLength={maxLength}
        className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
      />
    </div>
  );
}
