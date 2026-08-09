import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { getMyEntitlements, getProfile } from "@/lib/gateway";

export const metadata = { title: "My access" };

// Dynamic, cookie-dependent page — never cached (components.md §1 rendering table).
export default async function AccessPage() {
  const [t, tc] = await Promise.all([getTranslations("account"), getTranslations("common")]);
  const profile = await getProfile();
  if (!profile) {
    redirect("/login");
  }

  const entitlements = await getMyEntitlements();
  const active = new Set(["Active", "Trialing"]);
  const fmt = (iso: string | null) => (iso ? new Date(iso).toLocaleDateString() : tc("notSet"));

  return (
    <div className="max-w-2xl">
      <h1 className="text-xl font-semibold mb-1">{t("myAccess")}</h1>
      <p className="mb-4 text-sm text-neutral-500">{t("myAccessIntro")}</p>

      {entitlements.length === 0 ? (
        <p className="text-sm text-neutral-500">{t("noAccess")}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-neutral-200 text-left text-neutral-500">
                <th className="py-2 pr-4 font-medium">{t("accessType")}</th>
                <th className="py-2 pr-4 font-medium">{t("accessStatus")}</th>
                <th className="py-2 pr-4 font-medium">{t("accessGranted")}</th>
                <th className="py-2 font-medium">{t("accessExpires")}</th>
              </tr>
            </thead>
            <tbody>
              {entitlements.map((e) => (
                <tr key={e.id} className="border-b border-neutral-100">
                  <td className="py-2 pr-4">{e.type}</td>
                  <td className="py-2 pr-4">
                    <span className={active.has(e.status) ? "text-emerald-700" : "text-neutral-500"}>{e.status}</span>
                  </td>
                  <td className="py-2 pr-4">{fmt(e.startsAt)}</td>
                  <td className="py-2">{fmt(e.expiresAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
