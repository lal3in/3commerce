import { redirect } from "next/navigation";
import Link from "next/link";
import { getAddresses, getProfile, getMyOrders, getSavedPaymentMethods } from "@/lib/gateway";
import { formatMoney } from "@/lib/money";
import { logout } from "@/lib/auth-actions";
import { AddressForms } from "@/components/account/AccountForms";
import { CardEntryForm } from "@/components/account/CardEntryForm";

export const metadata = { title: "Account" };

// Dynamic, cookie-dependent page — never cached (components.md §1 rendering table).
export default async function AccountPage({ searchParams }: { searchParams: Promise<{ address?: string; card?: string }> }) {
  const profile = await getProfile();
  if (!profile) {
    redirect("/login");
  }
  const [orders, addresses, paymentMethods, status] = await Promise.all([getMyOrders(), getAddresses(), getSavedPaymentMethods(), searchParams]);

  return (
    <div className="max-w-2xl">
      <h1 className="text-xl font-semibold mb-4">Your account</h1>
      <dl className="space-y-2 text-sm">
        <div className="flex justify-between border-b border-neutral-100 py-2">
          <dt className="text-neutral-500">Email</dt>
          <dd>{profile.email}</dd>
        </div>
        <div className="flex justify-between border-b border-neutral-100 py-2">
          <dt className="text-neutral-500">Name</dt>
          <dd>{[profile.title, profile.firstName, profile.middleName, profile.lastName].filter(Boolean).join(" ") || "Not set"}</dd>
        </div>
        {profile.preferredName && (
          <div className="flex justify-between border-b border-neutral-100 py-2">
            <dt className="text-neutral-500">Preferred name</dt>
            <dd>{profile.preferredName}</dd>
          </div>
        )}
        <div className="flex justify-between border-b border-neutral-100 py-2">
          <dt className="text-neutral-500">Phone</dt>
          <dd>{profile.phone || "Not set"}</dd>
        </div>
        <div className="flex justify-between border-b border-neutral-100 py-2">
          <dt className="text-neutral-500">Date of birth</dt>
          <dd>{profile.dateOfBirth || "Not set"}</dd>
        </div>
        <div className="flex justify-between border-b border-neutral-100 py-2">
          <dt className="text-neutral-500">Email verified</dt>
          <dd>{profile.emailVerified ? "Yes" : "Pending"}</dd>
        </div>
      </dl>

      <h2 className="mt-8 text-lg font-semibold">Address book</h2>
      <AddressForms addresses={addresses} addressStatus={status.address} />

      <h2 className="mt-8 text-lg font-semibold">Saved cards</h2>
      {paymentMethods.length === 0 ? (
        <p className="mt-2 text-sm text-neutral-500">No saved cards yet. You can save one during checkout.</p>
      ) : (
        <ul className="mt-2 divide-y divide-neutral-100 text-sm">
          {paymentMethods.map((method) => (
            <li key={method.id} className="flex flex-wrap items-center justify-between gap-3 py-2">
              <div>
                <span>{method.brand.toUpperCase()} ending {method.last4}</span>
                <span className="ml-2 text-neutral-500">{method.expMonth}/{method.expYear}{method.isDefault ? " · default" : ""}</span>
              </div>
              <div className="flex gap-2">
                {!method.isDefault && (
                  <form action={`/account/payment-method/${method.id}`} method="post">
                    <button type="submit" name="action" value="make-default" className="rounded-md border border-neutral-300 px-3 py-1 text-xs">
                      Make default
                    </button>
                  </form>
                )}
                {!method.isDefault && (
                  <form action={`/account/payment-method/${method.id}`} method="post">
                    <button type="submit" name="action" value="delete" className="rounded-md border border-red-300 px-3 py-1 text-xs text-red-700">
                      Delete
                    </button>
                  </form>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}
      <CardEntryForm email={profile.email} cardStatus={status.card} />

      <h2 className="mt-8 text-lg font-semibold">Order history</h2>
      {/* FR-7: guest orders only attach to a VERIFIED email — be honest while it's pending. */}
      {!profile.emailVerified && (
        <p className="mt-2 rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
          Verify your email address ({profile.email}) using the link we sent you — orders you
          placed as a guest with that email will appear here once it&apos;s verified.
        </p>
      )}
      {orders.length === 0 ? (
        !profile.emailVerified ? null : <p className="mt-2 text-sm text-neutral-500">No orders yet.</p>
      ) : (
        <ul className="mt-2 divide-y divide-neutral-100 text-sm">
          {orders.map((o) => (
            <li key={o.id} className="flex items-center justify-between py-2">
              <span className="font-mono text-xs text-neutral-500">{o.id.slice(0, 8)}…</span>
              <span>{o.status}</span>
              <span>{formatMoney(o.grossMinor, o.currency)}</span>
              <Link href={`/orders/${o.id}/support`} className="underline text-neutral-500">
                Support
              </Link>
            </li>
          ))}
        </ul>
      )}

      <form action={logout} className="mt-6">
        <button type="submit" className="rounded-md border border-neutral-300 px-4 py-2 text-sm">
          Log out
        </button>
      </form>
    </div>
  );
}
