"use client";

import { useActionState, useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { payRenewal } from "@/lib/subscription-actions";
import { PayOutcome, type PayRenewalState } from "@/lib/subscription-pay";

interface PayRenewalFormProps {
  subscriptionId: string;
  idempotencyKey: string;
  amountLabel: string;
  cards: { id: string; label: string }[];
}

// The interactive leaf of the "complete payment" page: choose a card, pay, and — when the bank asks for 3-D Secure —
// complete the authentication. The storefront has no card SDK yet (checkout uses the same test step), so the
// authentication is the test-mode simulation; the provider's success event then settles the period.
export function PayRenewalForm({ subscriptionId, idempotencyKey, amountLabel, cards }: PayRenewalFormProps) {
  const t = useTranslations("subscriptions");
  const router = useRouter();
  const [state, action, pending] = useActionState<PayRenewalState, FormData>(payRenewal, {});
  const [authenticating, setAuthenticating] = useState(false);
  const [authenticated, setAuthenticated] = useState(false);

  if (state.outcome === PayOutcome.Paid || authenticated) {
    return (
      <p className="mt-6 rounded-md border border-green-200 bg-green-50 p-3 text-sm text-green-800" data-testid="pay-renewal-paid">
        {t("paid")}
      </p>
    );
  }

  if (state.outcome === PayOutcome.RequiresAction && state.paymentIntentId) {
    const intentId = state.paymentIntentId;
    return (
      <div className="mt-6 space-y-3 rounded-md border border-neutral-200 p-4 text-sm" data-testid="pay-renewal-authenticate">
        <h2 className="font-medium">{t("authenticateTitle")}</h2>
        <p className="text-neutral-600">{t("authenticateBody")}</p>
        <button
          type="button"
          disabled={authenticating}
          title={t("tips.authenticate")}
          onClick={async () => {
            setAuthenticating(true);
            const res = await fetch(`/api/dev-pay-intent/${intentId}`, { method: "POST" });
            setAuthenticating(false);
            if (res.ok) {
              setAuthenticated(true);
              router.refresh();
            }
          }}
          className="rounded-md bg-neutral-900 px-4 py-2 font-medium text-white disabled:opacity-50"
        >
          {authenticating ? t("authenticating") : t("authenticate")}
        </button>
      </div>
    );
  }

  const message =
    state.outcome === PayOutcome.Declined ? t("declined")
    : state.outcome === PayOutcome.ProviderUnavailable ? t("unavailable")
    : state.error === "notPastDue" ? t("notPastDue")
    : state.error === "unknownCard" ? t("unknownCard")
    : state.error === "failed" ? t("failed")
    : null;

  return (
    <form action={action} className="mt-6 space-y-3 text-sm">
      <input type="hidden" name="subscriptionId" value={subscriptionId} />
      {/* One key per submission: a double submit replays it; a deliberate retry after an answer gets a new one. */}
      <input type="hidden" name="idempotencyKey" value={`${idempotencyKey}-${state.attempt ?? 0}`} />
      {message && (
        <p role="alert" className="rounded-md bg-red-50 p-2 text-red-700" data-testid="pay-renewal-error">
          {message}
        </p>
      )}
      <label className="block font-medium" title={t("tips.card")}>
        {t("card")}
        <select
          name="savedPaymentMethodId"
          defaultValue=""
          aria-describedby="pay-renewal-card-hint"
          className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2"
        >
          <option value="">{t("currentCard")}</option>
          {cards.map((card) => (
            <option key={card.id} value={card.id}>
              {card.label}
            </option>
          ))}
        </select>
      </label>
      <p id="pay-renewal-card-hint" className="text-neutral-500">{t("cardHint")}</p>
      <button
        type="submit"
        disabled={pending}
        title={t("tips.pay")}
        className="rounded-md bg-neutral-900 px-4 py-2 font-medium text-white disabled:opacity-50"
        data-testid="pay-renewal-submit"
      >
        {pending ? t("paying") : t("pay", { amount: amountLabel })}
      </button>
    </form>
  );
}
