"use client";

import { useState, type InputHTMLAttributes } from "react";
import type { AddressDto } from "@/lib/gateway";

interface AddressFormsProps {
  addresses: AddressDto[];
  addressStatus?: string;
}

export function AddressForms({ addresses, addressStatus }: AddressFormsProps) {
  const [editingId, setEditingId] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const editing = addresses.find((address) => address.id === editingId);

  return (
    <div className="mt-2 space-y-4">
      {addressStatus === "error" && <p className="rounded-md bg-red-50 p-2 text-sm text-red-700">Could not save the address. Check the details and try again.</p>}
      {addressStatus === "saved" && <p className="rounded-md bg-green-50 p-2 text-sm text-green-700">Address saved.</p>}
      {addressStatus === "updated" && <p className="rounded-md bg-green-50 p-2 text-sm text-green-700">Address updated.</p>}
      {addressStatus === "deleted" && <p className="rounded-md bg-green-50 p-2 text-sm text-green-700">Address deleted.</p>}

      {addresses.length === 0 ? (
        <p className="text-sm text-neutral-500">No saved addresses yet.</p>
      ) : (
        <ul className="divide-y divide-neutral-100 text-sm">
          {addresses.map((address) => (
            <li key={address.id} className="flex flex-wrap items-start justify-between gap-3 py-3">
              <div>
                <div className="flex gap-2">
                  <span className="font-medium">{address.name}</span>
                  <span className="text-neutral-500">{address.purpose}{address.isDefault ? " · default" : ""}</span>
                </div>
                <p className="text-neutral-500">{address.line1}, {address.city} {address.postcode}, {address.country}</p>
              </div>
              <div className="flex gap-2">
                <button
                  type="button"
                  onClick={() => { setEditingId(address.id); setAdding(false); }}
                  className="rounded-md border border-neutral-300 px-3 py-1 text-xs"
                >
                  Edit
                </button>
                <form action={`/account/address/${address.id}`} method="post">
                  <button type="submit" name="action" value="delete" className="rounded-md border border-red-300 px-3 py-1 text-xs text-red-700">
                    Delete
                  </button>
                </form>
              </div>
            </li>
          ))}
        </ul>
      )}

      <button
        type="button"
        onClick={() => { setAdding(true); setEditingId(null); }}
        className="rounded-md border border-neutral-300 px-4 py-2 text-sm"
      >
        Add address
      </button>

      {editing && <AddressForm address={editing} onCancel={() => setEditingId(null)} />}
      {adding && <AddressForm onCancel={() => setAdding(false)} />}
    </div>
  );
}

function AddressForm({ address, onCancel }: { address?: AddressDto; onCancel: () => void }) {
  const action = address ? `/account/address/${address.id}` : "/account/address";
  return (
    <form action={action} method="post" className="rounded-md border border-neutral-200 p-4 space-y-3 text-sm">
      <h3 className="font-medium">{address ? "Update address" : "Add address"}</h3>
      <label className="block font-medium">
        Use
        <select name="purpose" defaultValue={address?.purpose ?? "Both"} className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2">
          <option value="Both">Shipping and billing</option>
          <option value="Shipping">Shipping only</option>
          <option value="Billing">Billing only</option>
        </select>
      </label>
      <Field name="name" label="Full name" autoComplete="name" defaultValue={address?.name ?? ""} required />
      <Field name="line1" label="Address" autoComplete="address-line1" defaultValue={address?.line1 ?? ""} required />
      <Field name="line2" label="Apartment, suite, etc." autoComplete="address-line2" defaultValue={address?.line2 ?? ""} />
      <div className="grid grid-cols-2 gap-3">
        <Field name="city" label="City" autoComplete="address-level2" defaultValue={address?.city ?? ""} required />
        <Field name="postcode" label="Postcode" autoComplete="postal-code" defaultValue={address?.postcode ?? ""} required />
      </div>
      <Field name="country" label="Country code" autoComplete="country" defaultValue={address?.country ?? "AU"} maxLength={2} required />
      <label className="flex items-center gap-2 text-neutral-700">
        <input name="isDefault" type="checkbox" className="h-4 w-4" defaultChecked={address?.isDefault ?? false} />
        Make default for this use
      </label>
      <div className="flex flex-wrap gap-2">
        <button type="submit" name="action" value="save" className="rounded-md bg-neutral-900 px-4 py-2 text-white">
          {address ? "Update address" : "Save address"}
        </button>
        <button type="button" onClick={onCancel} className="rounded-md border border-neutral-300 px-4 py-2">
          Cancel
        </button>
      </div>
    </form>
  );
}

function Field({ name, label, defaultValue = "", ...props }: {
  name: string;
  label: string;
  defaultValue?: string;
} & InputHTMLAttributes<HTMLInputElement>) {
  return (
    <label className="block font-medium">
      {label}
      <input
        name={name}
        defaultValue={defaultValue}
        className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2"
        {...props}
      />
    </label>
  );
}
