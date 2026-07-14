"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";

interface CardEntryFormProps {
  email: string;
  cardStatus?: string;
}

export function CardEntryForm({ email, cardStatus }: CardEntryFormProps) {
  const t = useTranslations("account");
  const [cardNumber, setCardNumber] = useState("");
  const [expiry, setExpiry] = useState("");
  const [cvv, setCvv] = useState("");

  return (
    <form action="/account/payment-method" method="post" className="mt-4 rounded-md border border-neutral-200 p-4 space-y-3 text-sm">
      <h3 className="font-medium">{t("addCard")}</h3>
      <p className="text-neutral-500">{t("cardIntro")}</p>
      {cardStatus === "error" && <p className="rounded-md bg-red-50 p-2 text-red-700">{t("cardError")}</p>}
      {cardStatus === "saved" && <p className="rounded-md bg-green-50 p-2 text-green-700">{t("cardSaved")}</p>}
      {cardStatus === "deleted" && <p className="rounded-md bg-green-50 p-2 text-green-700">{t("cardDeleted")}</p>}
      {cardStatus === "default" && <p className="rounded-md bg-green-50 p-2 text-green-700">{t("cardDefaultUpdated")}</p>}
      <input type="hidden" name="email" value={email} />
      <label className="block font-medium" title={t("tips.cardNumber")}>
        {t("cardNumber")}
        <input
          name="cardNumber"
          value={cardNumber}
          onChange={(event) => setCardNumber(event.target.value)}
          inputMode="numeric"
          autoComplete="cc-number"
          title={t("tips.cardNumber")}
          aria-describedby="cardNumber-tip"
          className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2"
          required
        />
        <span id="cardNumber-tip" className="sr-only">{t("tips.cardNumber")}</span>
      </label>
      <div className="grid grid-cols-2 gap-3">
        <label className="block font-medium" title={t("tips.expiry")}>
          {t("expiry")}
          <input
            name="expiry"
            value={expiry}
            onChange={(event) => setExpiry(event.target.value)}
            placeholder="MM/YY"
            autoComplete="cc-exp"
            title={t("tips.expiry")}
            aria-describedby="expiry-tip"
            className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2"
            required
          />
          <span id="expiry-tip" className="sr-only">{t("tips.expiry")}</span>
        </label>
        <label className="block font-medium" title={t("tips.cvv")}>
          {t("cvv")}
          <input
            name="cvv"
            value={cvv}
            onChange={(event) => setCvv(event.target.value)}
            inputMode="numeric"
            autoComplete="cc-csc"
            title={t("tips.cvv")}
            aria-describedby="cvv-tip"
            className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2"
            required
          />
          <span id="cvv-tip" className="sr-only">{t("tips.cvv")}</span>
        </label>
      </div>
      <label className="flex items-center gap-2 text-neutral-700" title={t("tips.makeDefaultCard")}>
        <input name="makeDefault" type="checkbox" className="h-4 w-4" title={t("tips.makeDefaultCard")} defaultChecked />
        {t("makeDefaultCard")}
      </label>
      <button type="submit" title={t("tips.saveCard")} className="rounded-md bg-neutral-900 px-4 py-2 text-white">
        {t("saveCard")}
      </button>
    </form>
  );
}
