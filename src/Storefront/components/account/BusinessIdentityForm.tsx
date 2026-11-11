"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { clearBusinessIdentity, saveBusinessIdentity } from "@/lib/business-identity-actions";
import type { BusinessIdentityDto, BusinessIdentityState } from "@/lib/business-identity";
import { businessFieldErrorKey, TAX_ID_SCHEME_OPTIONS, taxIdSchemeLabelKey } from "@/lib/tax-id-scheme";

interface BusinessIdentityFormProps {
  // The saved default business identity (Identity GET /me), or null when none is saved.
  identity: BusinessIdentityDto | null;
}

const IDLE: BusinessIdentityState = { status: "idle" };

/**
 * The account's default business identity (ADR-0063 P3b): the company the member usually buys for and its tax ID.
 * Checkout's "Business purchase" block is prefilled from it; each order still keeps its own snapshot. Identity
 * validates it with the same per-scheme rules checkout applies, and a refusal is explained in the member's language
 * (the shared `checkout.business.errors.*` messages) with the field marked `aria-invalid`.
 *
 * The inputs are uncontrolled with `defaultValue`s: after a refusal the form re-renders with what was submitted as the
 * defaults, so React's post-action form reset keeps the member's input (a `<select defaultValue>` resets to it too).
 */
export function BusinessIdentityForm({ identity }: BusinessIdentityFormProps) {
  const t = useTranslations("account");
  const tc = useTranslations("checkout");
  const [saveState, save, saving] = useActionState(saveBusinessIdentity, IDLE);
  const [clearState, clear, clearing] = useActionState(clearBusinessIdentity, IDLE);
  const refused = saveState.status === "error" ? saveState : null;
  const invalid = businessFieldErrorKey(refused?.field);
  const values = refused?.values ?? {
    companyName: identity?.companyName ?? "",
    taxId: identity?.taxId ?? "",
    taxIdScheme: identity?.taxIdScheme != null ? String(identity.taxIdScheme) : "",
    taxIdJurisdiction: identity?.taxIdJurisdiction ?? "",
  };
  const schemeLabel = taxIdSchemeLabelKey(identity?.taxIdScheme);
  // Remount the fields whenever the defaults change (a save, a clear, a refusal) so they show the new defaults.
  const fieldsKey = JSON.stringify(values);

  return (
    <div className="mt-2 space-y-3 text-sm" data-testid="business-identity">
      <p className="text-neutral-600">{t("businessIdentity.intro")}</p>
      {identity ? (
        <div className="rounded-md border border-neutral-200 p-3" data-testid="business-identity-saved">
          <p className="font-medium">{identity.companyName}</p>
          {identity.taxId && (
            <p className="text-neutral-600">
              {t("taxIdLine", { scheme: schemeLabel ? tc(`business.schemes.${schemeLabel}`) : "", taxId: identity.taxId })}
            </p>
          )}
          {identity.taxIdJurisdiction && (
            <p className="text-neutral-600">{t("businessIdentity.jurisdictionLine", { jurisdiction: identity.taxIdJurisdiction })}</p>
          )}
          <form action={clear} className="mt-2">
            <button
              type="submit"
              disabled={clearing}
              title={t("businessIdentity.tips.clear")}
              className="rounded-md border border-red-300 px-3 py-1 text-xs text-red-700 disabled:opacity-50"
            >
              {t("businessIdentity.clear")}
            </button>
          </form>
        </div>
      ) : (
        <p className="text-neutral-500">{t("businessIdentity.none")}</p>
      )}

      {saveState.status === "saved" && identity && <p role="status" className="text-green-700">{t("businessIdentity.saved")}</p>}
      {clearState.status === "cleared" && !identity && <p role="status" className="text-green-700">{t("businessIdentity.cleared")}</p>}
      {(refused || clearState.status === "error") && (
        <p role="alert" className="text-red-700" data-testid="business-identity-error">
          {invalid ? tc(`business.errors.${invalid}`) : t("businessIdentity.error")}
        </p>
      )}

      <form action={save} className="space-y-3">
        <div key={fieldsKey} className="space-y-3">
          <Field
            id="businessCompanyName"
            name="companyName"
            label={tc("business.companyName")}
            title={t("businessIdentity.tips.companyName")}
            defaultValue={values.companyName}
            autoComplete="organization"
            maxLength={200}
            required
            invalid={invalid === "companyName"}
          />
          <div className="grid grid-cols-2 gap-3">
            <Field
              id="businessTaxId"
              name="taxId"
              label={tc("business.taxId")}
              title={t("businessIdentity.tips.taxId")}
              defaultValue={values.taxId}
              maxLength={80}
              invalid={invalid === "taxId"}
            />
            <div>
              <label htmlFor="businessTaxIdScheme" className="block font-medium" title={t("businessIdentity.tips.taxIdScheme")}>
                {tc("business.taxIdScheme")}
              </label>
              <select
                id="businessTaxIdScheme"
                name="taxIdScheme"
                defaultValue={values.taxIdScheme}
                title={t("businessIdentity.tips.taxIdScheme")}
                aria-describedby="businessTaxIdScheme-tip"
                aria-invalid={invalid === "taxIdScheme" || undefined}
                className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 aria-[invalid]:border-red-600"
              >
                <option value="">{tc("business.chooseScheme")}</option>
                {TAX_ID_SCHEME_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>{tc(`business.schemes.${option.labelKey}`)}</option>
                ))}
              </select>
              <span id="businessTaxIdScheme-tip" className="sr-only">{t("businessIdentity.tips.taxIdScheme")}</span>
            </div>
          </div>
          <Field
            id="businessTaxIdJurisdiction"
            name="taxIdJurisdiction"
            label={t("businessIdentity.jurisdiction")}
            title={t("businessIdentity.tips.jurisdiction")}
            defaultValue={values.taxIdJurisdiction}
            maxLength={10}
            invalid={invalid === "taxIdJurisdiction"}
          />
        </div>
        <button
          type="submit"
          disabled={saving}
          title={t("businessIdentity.tips.save")}
          className="rounded-md bg-neutral-900 px-4 py-2 font-medium text-white disabled:opacity-50"
        >
          {t("businessIdentity.save")}
        </button>
      </form>
    </div>
  );
}

// A visible label, the localized tooltip as `title` and as sr-only help text tied via aria-describedby, and
// aria-invalid when Identity refused this input — the same contract as checkout's business fields.
function Field({ id, name, label, title, defaultValue, autoComplete, maxLength, required = false, invalid = false }: {
  id: string;
  name: string;
  label: string;
  title: string;
  defaultValue: string;
  autoComplete?: string;
  maxLength?: number;
  required?: boolean;
  invalid?: boolean;
}) {
  return (
    <div>
      <label htmlFor={id} className="block font-medium" title={title}>{label}</label>
      <input
        id={id}
        name={name}
        type="text"
        defaultValue={defaultValue}
        required={required}
        autoComplete={autoComplete}
        maxLength={maxLength}
        title={title}
        aria-describedby={`${id}-tip`}
        aria-invalid={invalid || undefined}
        className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 aria-[invalid]:border-red-600"
      />
      <span id={`${id}-tip`} className="sr-only">{title}</span>
    </div>
  );
}
