import { COUNTRIES } from "@/lib/countries";

// Shared country dropdown for every storefront address form (checkout, account). A native <select
// name=…> so it posts through the same server-action form flow the old text input used — the value
// is still the ISO 3166-1 alpha-2 code. Localized tooltip mirrors the Field pattern (title + sr-only).
export function CountrySelect({
  name,
  label,
  title,
  placeholder,
  defaultValue = "",
  required = false,
}: {
  name: string;
  label: string;
  title: string;
  placeholder: string;
  defaultValue?: string;
  required?: boolean;
}) {
  return (
    <div>
      <label htmlFor={name} className="block text-sm font-medium" title={title}>{label}</label>
      <select
        id={name}
        name={name}
        required={required}
        defaultValue={defaultValue}
        autoComplete="country"
        title={title}
        aria-describedby={`${name}-tip`}
        className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
      >
        <option value="">{placeholder}</option>
        {COUNTRIES.map((c) => (
          <option key={c.code} value={c.code}>{c.name}</option>
        ))}
      </select>
      <span id={`${name}-tip`} className="sr-only">{title}</span>
    </div>
  );
}
