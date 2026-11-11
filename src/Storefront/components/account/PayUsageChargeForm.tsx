"use client";

import { useActionState, useState, type ReactNode } from "react";
import { useTranslations } from "next-intl";
import { payUsageCharge } from "@/lib/usage-charge-actions";
import { UsagePayOutcome, type PayUsageChargeState } from "@/lib/usage-charge-pay";

interface PayUsageChargeFormProps {
  reference: string;
  idempotencyKey: string;
  amountLabel: string;
  cards: { id: string; label: string }[];
  /** What is owed (server-rendered): shown until the charge is paid, then replaced by the confirmation. */
  children?: ReactNode;
}

// The interactive leaf of the usage "complete payment" page: choose a card, pay, and — when the bank asks for 3-D Secure —
// complete the authentication. The storefront has no card SDK yet (checkout and the subscription pay page use the same
// test step), so the authentication is the test-mode simulation; the provider's success event then settles the charge.
export function PayUsageChargeForm({ reference, idempotencyKey, amountLabel, cards, children }: PayUsageChargeFormProps) {
  const t = useTranslations("usageCharges");
  const [state, action, pending] = useActionState<PayUsageChargeState, FormData>(payUsageCharge, {});
  const [authenticating, setAuthenticating] = useState(false);
  const [authenticated, setAuthenticated] = useState(false);

  // Once paid, ONLY the confirmation shows — never the stale amount due (the #315 lesson).
  if (state.outcome === UsagePayOutcome.Paid || authenticated) {
    return (
      <p className="mt-6 rounded-md border border-green-200 bg-green-50 p-3 text-sm text-green-800" data-testid="pay-usage-paid">
        {t("paid")}
      </p>
    );
  }

  if (state.outcome === UsagePayOutcome.RequiresAction && state.paymentIntentId) {
    const intentId = state.paymentIntentId;
    return (
      <>
        {children}
        <div className="mt-6 space-y-3 rounded-md border border-neutral-200 p-4 text-sm" data-testid="pay-usage-authenticate">
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
              // No router.refresh(): the refreshed page would find the charge settled and replace this form (and its
              // confirmation) with "nothing to pay".
              if (res.ok) {
                setAuthenticated(true);
              }
            }}
            className="rounded-md bg-neutral-900 px-4 py-2 font-medium text-white disabled:opacity-50"
          >
            {authenticating ? t("authenticating") : t("authenticate")}
          </button>
        </div>
      </>
    );
  }

  const message =
    state.outcome === UsagePayOutcome.Declined
      ? t("declined")
      : state.outcome === UsagePayOutcome.ProviderUnavailable
        ? t("unavailable")
        : state.error === "notPayable"
          ? t("notPayable")
          : state.error === "unknownCard"
            ? t("unknownCard")
            : state.error === "failed"
              ? t("failed")
              : null;

  return (
    <>
      {children}
      <form action={action} className="mt-6 space-y-3 text-sm">
        <input type="hidden" name="reference" value={reference} />
        {/* One key per submission: a double submit replays it; a deliberate retry after an answer gets a new one. */}
        <input type="hidden" name="idempotencyKey" value={`${idempotencyKey}-${state.attempt ?? 0}`} />
        {message && (
          <p role="alert" className="rounded-md bg-red-50 p-2 text-red-700" data-testid="pay-usage-error">
            {message}
          </p>
        )}
        <label className="block font-medium" title={t("tips.card")}>
          {t("card")}
          <select
            name="savedPaymentMethodId"
            defaultValue=""
            aria-describedby="pay-usage-card-hint"
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
        <p id="pay-usage-card-hint" className="text-neutral-500">
          {t("cardHint")}
        </p>
        <button
          type="submit"
          disabled={pending}
          title={t("tips.pay")}
          className="rounded-md bg-neutral-900 px-4 py-2 font-medium text-white disabled:opacity-50"
          data-testid="pay-usage-submit"
        >
          {pending ? t("paying") : t("pay", { amount: amountLabel })}
        </button>
      </form>
    </>
  );
}
