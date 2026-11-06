"use client";

import { useEffect, useRef, useState } from "react";
import { useTranslations } from "next-intl";
import { businessFieldErrorKey, TAX_ID_SCHEME_OPTIONS, TaxIdScheme } from "@/lib/tax-id-scheme";
import type { BusinessIdentityDto } from "@/lib/business-identity";

interface BusinessPurchaseProps {
  // The request field Ordering's last refusal named (problem+json `field`, e.g. "buyer.taxId"), if any.
  errorField?: string;
  // The signed-in member's saved default business identity (ADR-0063 P3b), if any. It PREFILLS the block (ticked,
  // fields filled); the shopper can still change or untick it for this order, and the order keeps what is posted.
  defaults?: BusinessIdentityDto | null;
}

/**
 * "Business purchase" at checkout (ADR-0063 P3, § 8): an optional company name + tax ID + the register it belongs to,
 * printed on the invoice. It never changes the price or the amount charged — no reverse charge until phase 2 —
 * and Ordering checks only the FORMAT per scheme (VIES lookup is phase 2).
 *
 * A refused checkout must keep what the shopper typed: React resets the form after every form action, and that
 * reset rewrites the DOM of these controlled inputs too (the checkbox unticked, the scheme back to "Select")
 * while React state still holds the values. So the values are SUBMITTED from hidden inputs (a reset leaves a
 * hidden input's value alone) and the visible controls carry no `name`; on the form's `reset` event they are
 * remounted, so what the shopper sees is what is posted.
 */
export function BusinessPurchase({ errorField, defaults }: BusinessPurchaseProps) {
  const t = useTranslations("checkout");
  const [enabled, setEnabled] = useState(Boolean(defaults));
  const [companyName, setCompanyName] = useState(defaults?.companyName ?? "");
  const [taxId, setTaxId] = useState(defaults?.taxId ?? "");
  const [scheme, setScheme] = useState(defaults?.taxIdScheme != null ? String(defaults.taxIdScheme) : "");
  const [jurisdiction, setJurisdiction] = useState(defaults?.taxIdJurisdiction ?? "");
  const invalid = businessFieldErrorKey(errorField);
  const usState = scheme === String(TaxIdScheme.UsStateSalesTaxId);
  // A saved identity's jurisdiction can be narrower than the one its scheme implies (a Canadian BN for CA-QC). While
  // the number and its type are still the saved ones it is posted too, so the order snapshot matches the profile.
  const savedJurisdiction = Boolean(defaults?.taxIdJurisdiction)
    && scheme === String(defaults?.taxIdScheme ?? "")
    && taxId === (defaults?.taxId ?? "");
  const postJurisdiction = usState || savedJurisdiction;
  const sectionRef = useRef<HTMLElement>(null);
  const [mount, setMount] = useState(0);
  useEffect(() => {
    const form = sectionRef.current?.closest("form");
    if (!form) return;
    // Re-render the visible controls from state after the post-action reset has rewritten their DOM.
    const remount = () => setMount((value) => value + 1);
    form.addEventListener("reset", remount);
    return () => form.removeEventListener("reset", remount);
  }, []);

  return (
    <section ref={sectionRef} className="rounded-md border border-neutral-200 p-4 space-y-3" aria-labelledby="business-purchase-heading">
      {enabled && (
        <>
          <input type="hidden" name="businessPurchase" value="on" />
          <input type="hidden" name="buyerCompanyName" value={companyName} />
          <input type="hidden" name="buyerTaxId" value={taxId} />
          <input type="hidden" name="buyerTaxIdScheme" value={scheme} />
          {postJurisdiction && <input type="hidden" name="buyerTaxIdJurisdiction" value={jurisdiction} />}
        </>
      )}
      <h2 id="business-purchase-heading" className="font-medium">{t("business.title")}</h2>
      <label key={`toggle-${mount}`} className="flex items-center gap-2 text-sm" title={t("tips.businessPurchase")}>
        <input
          type="checkbox"
          checked={enabled}
          onChange={(event) => setEnabled(event.currentTarget.checked)}
          title={t("tips.businessPurchase")}
          className="h-4 w-4"
        />
        {t("business.toggle")}
      </label>
      {enabled && (
        <div key={`fields-${mount}`} className="space-y-3" data-testid="business-purchase">
          {defaults && (
            <p className="text-sm text-neutral-600" data-testid="business-purchase-prefilled">{t("business.prefilled")}</p>
          )}
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
// and as sr-only help text tied via aria-describedby, and aria-invalid when Ordering refused this input. No `name`:
// the value is posted by the section's hidden input (see BusinessPurchase).
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
