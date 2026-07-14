"use client";

import { useState, type InputHTMLAttributes } from "react";
import { useTranslations } from "next-intl";
import type { AddressDto } from "@/lib/gateway";

interface AddressFormsProps {
  addresses: AddressDto[];
  addressStatus?: string;
}

export function AddressForms({ addresses, addressStatus }: AddressFormsProps) {
  const t = useTranslations("account");
  const tc = useTranslations("common");
  const [editingId, setEditingId] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const editing = addresses.find((address) => address.id === editingId);

  return (
    <div className="mt-2 space-y-4">
      {addressStatus === "error" && <p className="rounded-md bg-red-50 p-2 text-sm text-red-700">{t("addressError")}</p>}
      {addressStatus === "saved" && <p className="rounded-md bg-green-50 p-2 text-sm text-green-700">{t("addressSaved")}</p>}
      {addressStatus === "updated" && <p className="rounded-md bg-green-50 p-2 text-sm text-green-700">{t("addressUpdated")}</p>}
      {addressStatus === "deleted" && <p className="rounded-md bg-green-50 p-2 text-sm text-green-700">{t("addressDeleted")}</p>}

      {addresses.length === 0 ? (
        <p className="text-sm text-neutral-500">{t("noAddresses")}</p>
      ) : (
        <ul className="divide-y divide-neutral-100 text-sm">
          {addresses.map((address) => (
            <li key={address.id} className="flex flex-wrap items-start justify-between gap-3 py-3">
              <div>
                <div className="flex gap-2">
                  <span className="font-medium">{address.name}</span>
                  <span className="text-neutral-500">
                    {t(`purpose${address.purpose}`)}
                    {address.isDefault ? ` · ${tc("default")}` : ""}
                  </span>
                </div>
                <p className="text-neutral-500">{address.line1}, {address.city} {address.postcode}, {address.country}</p>
              </div>
              <div className="flex gap-2">
                <button
                  type="button"
                  title={t("tips.editAddress")}
                  onClick={() => { setEditingId(address.id); setAdding(false); }}
                  className="rounded-md border border-neutral-300 px-3 py-1 text-xs"
                >
                  {tc("edit")}
                </button>
                <form action={`/account/address/${address.id}`} method="post">
                  <button
                    type="submit"
                    name="action"
                    value="delete"
                    title={t("tips.deleteAddress")}
                    className="rounded-md border border-red-300 px-3 py-1 text-xs text-red-700"
                  >
                    {t("delete")}
                  </button>
                </form>
              </div>
            </li>
          ))}
        </ul>
      )}

      <button
        type="button"
        title={t("tips.addAddress")}
        onClick={() => { setAdding(true); setEditingId(null); }}
        className="rounded-md border border-neutral-300 px-4 py-2 text-sm"
      >
        {t("addAddress")}
      </button>

      {editing && <AddressForm address={editing} onCancel={() => setEditingId(null)} />}
      {adding && <AddressForm onCancel={() => setAdding(false)} />}
    </div>
  );
}

function AddressForm({ address, onCancel }: { address?: AddressDto; onCancel: () => void }) {
  const t = useTranslations("account");
  const tc = useTranslations("common");
  const action = address ? `/account/address/${address.id}` : "/account/address";
  return (
    <form action={action} method="post" className="rounded-md border border-neutral-200 p-4 space-y-3 text-sm">
      <h3 className="font-medium">{address ? t("updateAddress") : t("addAddress")}</h3>
      <label className="block font-medium" title={t("tips.addressUse")}>
        {t("addressUse")}
        <select
          name="purpose"
          defaultValue={address?.purpose ?? "Both"}
          title={t("tips.addressUse")}
          className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2"
        >
          <option value="Both">{t("purposeBoth")}</option>
          <option value="Shipping">{t("purposeShipping")}</option>
          <option value="Billing">{t("purposeBilling")}</option>
        </select>
      </label>
      <Field name="name" label={t("fullName")} tip={t("tips.fullName")} autoComplete="name" defaultValue={address?.name ?? ""} required />
      <Field name="line1" label={t("addressLine1")} tip={t("tips.addressLine1")} autoComplete="address-line1" defaultValue={address?.line1 ?? ""} required />
      <Field name="line2" label={t("addressLine2")} tip={t("tips.addressLine2")} autoComplete="address-line2" defaultValue={address?.line2 ?? ""} />
      <div className="grid grid-cols-2 gap-3">
        <Field name="city" label={t("city")} tip={t("tips.city")} autoComplete="address-level2" defaultValue={address?.city ?? ""} required />
        <Field name="postcode" label={t("postcode")} tip={t("tips.postcode")} autoComplete="postal-code" defaultValue={address?.postcode ?? ""} required />
      </div>
      <Field name="country" label={t("countryCode")} tip={t("tips.countryCode")} autoComplete="country" defaultValue={address?.country ?? "AU"} maxLength={2} required />
      <label className="flex items-center gap-2 text-neutral-700" title={t("tips.makeDefaultAddress")}>
        <input
          name="isDefault"
          type="checkbox"
          className="h-4 w-4"
          title={t("tips.makeDefaultAddress")}
          defaultChecked={address?.isDefault ?? false}
        />
        {t("makeDefaultAddress")}
      </label>
      <div className="flex flex-wrap gap-2">
        <button type="submit" name="action" value="save" className="rounded-md bg-neutral-900 px-4 py-2 text-white">
          {address ? t("updateAddress") : t("saveAddress")}
        </button>
        <button type="button" onClick={onCancel} className="rounded-md border border-neutral-300 px-4 py-2">
          {tc("cancel")}
        </button>
      </div>
    </form>
  );
}

// Localized tooltip on every field: `title` (hover) + an sr-only description tied via aria-describedby.
function Field({ name, label, tip, defaultValue = "", ...props }: {
  name: string;
  label: string;
  tip: string;
  defaultValue?: string;
} & InputHTMLAttributes<HTMLInputElement>) {
  return (
    <label className="block font-medium" title={tip}>
      {label}
      <input
        name={name}
        defaultValue={defaultValue}
        title={tip}
        aria-describedby={`${name}-tip`}
        className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2"
        {...props}
      />
      <span id={`${name}-tip`} className="sr-only">{tip}</span>
    </label>
  );
}
