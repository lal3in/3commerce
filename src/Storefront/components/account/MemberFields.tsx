// Structured member profile inputs (mem_1) reused by registration, the post-checkout account
// offer (pre-filled from the order), and the account profile editor. Title/Middle/Preferred are
// optional; First/Last/Phone/DOB are the required member details for recurrent-payment services.

export const TITLES = ["", "Mr", "Mrs", "Ms", "Miss", "Mx", "Dr", "Prof"] as const;

export type MemberDefaults = {
  title?: string | null;
  firstName?: string | null;
  middleName?: string | null;
  lastName?: string | null;
  preferredName?: string | null;
  phone?: string | null;
  dateOfBirth?: string | null;
  marketingConsent?: boolean | null;
};

const field = "mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm";

export function MemberFields({ defaults }: { defaults?: MemberDefaults }) {
  return (
    <div className="space-y-3">
      <div className="grid grid-cols-3 gap-3">
        <div>
          <label htmlFor="title" className="block text-sm font-medium">
            Title
          </label>
          <select id="title" name="title" defaultValue={defaults?.title ?? ""} className={field}>
            {TITLES.map((t) => (
              <option key={t} value={t}>
                {t || "—"}
              </option>
            ))}
          </select>
        </div>
        <div className="col-span-2">
          <label htmlFor="firstName" className="block text-sm font-medium">
            First name <span className="text-red-600">*</span>
          </label>
          <input id="firstName" name="firstName" required autoComplete="given-name" defaultValue={defaults?.firstName ?? ""} className={field} />
        </div>
      </div>
      <div className="grid grid-cols-2 gap-3">
        <div>
          <label htmlFor="middleName" className="block text-sm font-medium">
            Middle name(s)
          </label>
          <input id="middleName" name="middleName" autoComplete="additional-name" defaultValue={defaults?.middleName ?? ""} className={field} />
        </div>
        <div>
          <label htmlFor="lastName" className="block text-sm font-medium">
            Last name <span className="text-red-600">*</span>
          </label>
          <input id="lastName" name="lastName" required autoComplete="family-name" defaultValue={defaults?.lastName ?? ""} className={field} />
        </div>
      </div>
      <div>
        <label htmlFor="preferredName" className="block text-sm font-medium">
          Preferred name <span className="text-neutral-400">(optional)</span>
        </label>
        <input id="preferredName" name="preferredName" autoComplete="nickname" defaultValue={defaults?.preferredName ?? ""} className={field} />
      </div>
      <div className="grid grid-cols-2 gap-3">
        <div>
          <label htmlFor="phone" className="block text-sm font-medium">
            Phone <span className="text-red-600">*</span>
          </label>
          <input id="phone" name="phone" type="tel" required autoComplete="tel" defaultValue={defaults?.phone ?? ""} className={field} />
        </div>
        <div>
          <label htmlFor="dateOfBirth" className="block text-sm font-medium">
            Date of birth <span className="text-red-600">*</span>
          </label>
          <input id="dateOfBirth" name="dateOfBirth" type="date" required defaultValue={defaults?.dateOfBirth ?? ""} className={field} />
        </div>
      </div>
      <label className="flex items-center gap-2 text-sm">
        <input type="checkbox" name="marketingConsent" defaultChecked={defaults?.marketingConsent ?? false} className="h-4 w-4" />
        Email me product news and offers
      </label>
    </div>
  );
}
