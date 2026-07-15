import { redirect } from "next/navigation";
import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { getAddresses, getProfile, getMyOrders, getSavedPaymentMethods } from "@/lib/gateway";
import { formatMoney } from "@/lib/money";
import { logout } from "@/lib/auth-actions";
import { AddressForms } from "@/components/account/AccountForms";
import { CardEntryForm } from "@/components/account/CardEntryForm";

export const metadata = { title: "Account" };

// Dynamic, cookie-dependent page — never cached (components.md §1 rendering table).
export default async function AccountPage({ searchParams }: { searchParams: Promise<{ address?: string; card?: string }> }) {
  const [t, tc] = await Promise.all([getTranslations("account"), getTranslations("common")]);
  const profile = await getProfile();
  if (!profile) {
    redirect("/login");
  }
  const [orders, addresses, paymentMethods, status] = await Promise.all([getMyOrders(), getAddresses(), getSavedPaymentMethods(), searchParams]);

  return (
    <div className="max-w-2xl">
      <h1 className="text-xl font-semibold mb-4">{t("title")}</h1>
      <dl className="space-y-2 text-sm">
        <div className="flex justify-between border-b border-neutral-100 py-2">
          <dt className="text-neutral-500">{t("email")}</dt>
          <dd>{profile.email}</dd>
        </div>
        <div className="flex justify-between border-b border-neutral-100 py-2">
          <dt className="text-neutral-500">{t("name")}</dt>
          <dd>{[profile.title, profile.firstName, profile.middleName, profile.lastName].filter(Boolean).join(" ") || tc("notSet")}</dd>
        </div>
        {profile.preferredName && (
          <div className="flex justify-between border-b border-neutral-100 py-2">
            <dt className="text-neutral-500">{t("preferredName")}</dt>
            <dd>{profile.preferredName}</dd>
          </div>
        )}
        <div className="flex justify-between border-b border-neutral-100 py-2">
          <dt className="text-neutral-500">{t("phone")}</dt>
          <dd>{profile.phone || tc("notSet")}</dd>
        </div>
        <div className="flex justify-between border-b border-neutral-100 py-2">
          <dt className="text-neutral-500">{t("dateOfBirth")}</dt>
          <dd>{profile.dateOfBirth || tc("notSet")}</dd>
        </div>
        <div className="flex justify-between border-b border-neutral-100 py-2">
          <dt className="text-neutral-500">{t("emailVerified")}</dt>
          <dd>{profile.emailVerified ? tc("yes") : tc("pending")}</dd>
        </div>
      </dl>

      <h2 className="mt-8 text-lg font-semibold">{t("addressBook")}</h2>
      <AddressForms addresses={addresses} addressStatus={status.address} />

      <h2 className="mt-8 text-lg font-semibold">{t("savedCards")}</h2>
      {paymentMethods.length === 0 ? (
        <p className="mt-2 text-sm text-neutral-500">{t("noSavedCards")}</p>
      ) : (
        <ul className="mt-2 divide-y divide-neutral-100 text-sm">
          {paymentMethods.map((method) => (
            <li key={method.id} className="flex flex-wrap items-center justify-between gap-3 py-2">
              <div>
                <span>{t("cardSummary", { brand: method.brand.toUpperCase(), last4: method.last4 })}</span>
                <span className="ml-2 text-neutral-500">
                  {t(method.isDefault ? "cardExpiryDefault" : "cardExpiry", { expMonth: method.expMonth, expYear: method.expYear })}
                </span>
              </div>
              <div className="flex gap-2">
                {!method.isDefault && (
                  <form action={`/account/payment-method/${method.id}`} method="post">
                    <button
                      type="submit"
                      name="action"
                      value="make-default"
                      title={t("tips.makeDefaultCardAction")}
                      className="rounded-md border border-neutral-300 px-3 py-1 text-xs"
                    >
                      {t("makeDefault")}
                    </button>
                  </form>
                )}
                {!method.isDefault && (
                  <form action={`/account/payment-method/${method.id}`} method="post">
                    <button
                      type="submit"
                      name="action"
                      value="delete"
                      title={t("tips.deleteCard")}
                      className="rounded-md border border-red-300 px-3 py-1 text-xs text-red-700"
                    >
                      {t("delete")}
                    </button>
                  </form>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}
      <CardEntryForm email={profile.email} cardStatus={status.card} />

      <h2 className="mt-8 text-lg font-semibold">{t("orderHistory")}</h2>
      {/* FR-7: guest orders only attach to a VERIFIED email — be honest while it's pending. */}
      {!profile.emailVerified && (
        <p className="mt-2 rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
          {t("verifyEmailNotice", { email: profile.email })}
        </p>
      )}
      {orders.length === 0 ? (
        !profile.emailVerified ? null : <p className="mt-2 text-sm text-neutral-500">{t("noOrders")}</p>
      ) : (
        <ul className="mt-2 divide-y divide-neutral-100 text-sm">
          {orders.map((o) => (
            <li key={o.id} className="flex items-center justify-between py-2">
              <span className="font-mono text-xs text-neutral-500">{o.id.slice(0, 8)}…</span>
              <span>{o.status}</span>
              <span>{formatMoney(o.grossMinor, o.currency)}</span>
              <Link href={`/orders/${o.id}/support`} title={t("tips.support")} className="underline text-neutral-500">
                {t("support")}
              </Link>
            </li>
          ))}
        </ul>
      )}

      <form action={logout} className="mt-6">
        <button type="submit" title={t("tips.logOut")} className="rounded-md border border-neutral-300 px-4 py-2 text-sm">
          {t("logOut")}
        </button>
      </form>
    </div>
  );
}
