"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { businessFieldErrorKey, TAX_ID_SCHEME_OPTIONS, TaxIdScheme } from "@/lib/tax-id-scheme";

interface BusinessPurchaseProps {
  // The request field Ordering's last refusal named (problem+json `field`, e.g. "buyer.taxId"), if any.
  errorField?: string;
}

/**
 * "Business purchase" at checkout (ADR-0063 P3, § 8): an optional company name + tax ID + the register it belongs to,
 * printed on the invoice. It never changes the price or the amount charged — no reverse charge until phase 2 —
 * and Ordering checks only the FORMAT per scheme (VIES lookup is phase 2). The inputs are controlled so a refused
 * checkout (React resets uncontrolled fields after a form action) keeps what the shopper typed for the fix.
 */
export function BusinessPurchase({ errorField }: BusinessPurchaseProps) {
  const t = useTranslations("checkout");
  const [enabled, setEnabled] = useState(false);
  const [companyName, setCompanyName] = useState("");
  const [taxId, setTaxId] = useState("");
  const [scheme, setScheme] = useState("");
  const [jurisdiction, setJurisdiction] = useState("");
  const invalid = businessFieldErrorKey(errorField);
  const usState = scheme === String(TaxIdScheme.UsStateSalesTaxId);

  return (
    <section className="rounded-md border border-neutral-200 p-4 space-y-3" aria-labelledby="business-purchase-heading">
      <h2 id="business-purchase-heading" className="font-medium">{t("business.title")}</h2>
      <label className="flex items-center gap-2 text-sm" title={t("tips.businessPurchase")}>
        <input
          type="checkbox"
          name="businessPurchase"
          checked={enabled}
          onChange={(event) => setEnabled(event.currentTarget.checked)}
          title={t("tips.businessPurchase")}
          className="h-4 w-4"
        />
        {t("business.toggle")}
      </label>
      {enabled && (
        <div className="space-y-3" data-testid="business-purchase">
          <TextInput
            name="buyerCompanyName"
            label={t("business.companyName")}
            title={t("tips.companyName")}
            value={companyName}
            onChange={setCompanyName}
            autoComplete="organization"
            maxLength={200}
            required
            invalid={invalid === "companyName"}
          />
          <div className="grid grid-cols-2 gap-3">
            <TextInput
              name="buyerTaxId"
              label={t("business.taxId")}
              title={t("tips.taxId")}
              value={taxId}
              onChange={setTaxId}
              maxLength={80}
              invalid={invalid === "taxId"}
            />
            <div>
              <label htmlFor="buyerTaxIdScheme" className="block text-sm font-medium" title={t("tips.taxIdScheme")}>
                {t("business.taxIdScheme")}
              </label>
              <select
                id="buyerTaxIdScheme"
                name="buyerTaxIdScheme"
                value={scheme}
                // A tax ID is only meaningful with its register; without one, the type is not needed.
                required={taxId.trim().length > 0}
                title={t("tips.taxIdScheme")}
                aria-describedby="buyerTaxIdScheme-tip"
                aria-invalid={invalid === "taxIdScheme" || undefined}
                onChange={(event) => setScheme(event.currentTarget.value)}
                className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm aria-[invalid]:border-red-600"
              >
                <option value="">{t("business.chooseScheme")}</option>
                {TAX_ID_SCHEME_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>{t(`business.schemes.${option.labelKey}`)}</option>
                ))}
              </select>
              <span id="buyerTaxIdScheme-tip" className="sr-only">{t("tips.taxIdScheme")}</span>
            </div>
          </div>
          {(usState || invalid === "taxIdJurisdiction") && (
            <TextInput
              name="buyerTaxIdJurisdiction"
              label={t("business.taxIdJurisdiction")}
              title={t("tips.taxIdJurisdiction")}
              value={jurisdiction}
              onChange={setJurisdiction}
              maxLength={10}
              invalid={invalid === "taxIdJurisdiction"}
            />
          )}
          <p className="text-sm text-neutral-500">{t("business.note")}</p>
        </div>
      )}
    </section>
  );
}

// Same accessibility contract as the checkout's other fields: a visible label, the localized tooltip as `title`
// and as sr-only help text tied via aria-describedby, and aria-invalid when Ordering refused this input.
function TextInput({ name, label, title, value, onChange, autoComplete, maxLength, required = false, invalid = false }: {
  name: string;
  label: string;
  title: string;
  value: string;
  onChange: (value: string) => void;
  autoComplete?: string;
  maxLength?: number;
  required?: boolean;
  invalid?: boolean;
}) {
  return (
    <div>
      <label htmlFor={name} className="block text-sm font-medium" title={title}>{label}</label>
      <input
        id={name}
        name={name}
        type="text"
        value={value}
        required={required}
        autoComplete={autoComplete}
        maxLength={maxLength}
        title={title}
        aria-describedby={`${name}-tip`}
        aria-invalid={invalid || undefined}
        onChange={(event) => onChange(event.currentTarget.value)}
        className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm aria-[invalid]:border-red-600"
      />
      <span id={`${name}-tip`} className="sr-only">{title}</span>
    </div>
  );
}
