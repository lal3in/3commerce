import Link from "next/link";
import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { getMyUsageCharge, getProfile, getSavedPaymentMethods } from "@/lib/gateway";
import { formatMoney } from "@/lib/money";
import { LocalTime } from "@/components/LocalTime";
import { PayUsageChargeForm } from "@/components/account/PayUsageChargeForm";
import { MeterKeys, UsageChargeKind } from "@/lib/usage-charge-pay";

export const metadata = { title: "Complete payment" };

// The usage "complete payment" page (usage_charge_pay_page): linked from the usage dunning emails and the account page.
// Shows what an unpaid overage / auto top-up charge owes and lets the customer pay it now — on-session, so the bank may
// ask for 3-D Secure — optionally on another saved card. Paying it lifts the hold on the customer's usage. Dynamic,
// cookie-dependent — never cached (components.md §1).
export default async function PayUsageChargePage({ params }: { params: Promise<{ reference: string }> }) {
  const [t, profile] = await Promise.all([getTranslations("usageCharges"), getProfile()]);
  if (!profile) {
    redirect("/login");
  }

  const { reference } = await params;
  const charge = await getMyUsageCharge(decodeURIComponent(reference));
  if (!charge) {
    return (
      <div className="max-w-xl">
        <h1 className="text-xl font-semibold mb-4">{t("pageTitle")}</h1>
        <p className="text-sm text-neutral-600">{t("notFound")}</p>
        <Link href="/account" className="mt-4 inline-block text-sm underline">
          {t("backToAccount")}
        </Link>
      </div>
    );
  }

  const cards = await getSavedPaymentMethods();
  const payable = charge.status === "PastDue" || charge.status === "Abandoned";
  const amountDue = charge.amountDueMinor ?? charge.priceMinor;
  const autoLoad = charge.kind === UsageChargeKind.AutoLoad;
  const meter = t(`meter.${MeterKeys[charge.meter] ?? "Token"}`);
  const reasonKey = (["RequiresAction", "Failed", "Cancelled", "Error"] as const).find((r) => r === charge.failureReason);
  const statusKey = (["Pending", "Settled", "PastDue", "Abandoned", "Skipped"] as const).find((s) => s === charge.status) ?? "Settled";

  return (
    <div className="max-w-xl" data-testid="pay-usage">
      <h1 className="text-xl font-semibold mb-2">{t("pageTitle")}</h1>
      {payable ? (
        <PayUsageChargeForm
          reference={charge.reference}
          idempotencyKey={crypto.randomUUID()}
          amountLabel={formatMoney(amountDue, charge.currency)}
          cards={cards.map((c) => ({
            id: c.id,
            label: t("savedCard", { brand: c.brand.toUpperCase(), last4: c.last4 }),
          }))}
        >
          {/* What is owed: the form shows it until the charge is paid, then replaces it with the confirmation. */}
          <p className="mb-4 text-sm text-neutral-600">{t("intro")}</p>
          {reasonKey && (
            <p
              className="mb-4 rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800"
              data-testid="pay-usage-reason"
            >
              {t(`reason.${reasonKey}`)}
            </p>
          )}
          {charge.status === "Abandoned" ? (
            <p className="mb-4 text-sm text-red-700" data-testid="pay-usage-suspended">{t("suspended", { meter })}</p>
          ) : (
            autoLoad && <p className="mb-4 text-sm text-neutral-600">{t("creditOnHold")}</p>
          )}
          <dl className="space-y-2 text-sm">
            <div className="flex justify-between border-b border-neutral-100 py-2">
              <dt className="text-neutral-500">{t("item")}</dt>
              <dd data-testid="pay-usage-item">
                {t(autoLoad ? "kind.AutoLoad" : "kind.Overage", { meter })} · {t("units", { quantity: charge.quantity })}
              </dd>
            </div>
            <div className="flex justify-between border-b border-neutral-100 py-2">
              <dt className="text-neutral-500">{t("chargedOn")}</dt>
              <dd>
                <LocalTime iso={charge.createdAt} dateOnly />
              </dd>
            </div>
            <div className="flex justify-between border-b border-neutral-100 py-2 font-medium">
              <dt>{t("amountDue")}</dt>
              <dd data-testid="pay-usage-amount">{formatMoney(amountDue, charge.currency)}</dd>
            </div>
          </dl>
          {charge.nextRetryAt && (
            <p className="mt-3 text-sm text-neutral-600">
              {t.rich("nextRetry", { date: () => <LocalTime iso={charge.nextRetryAt!} dateOnly /> })}
            </p>
          )}
          {charge.abandonsAfter && (
            <p className="mt-1 text-sm text-neutral-600">
              {t.rich("abandonsAfter", { date: () => <LocalTime iso={charge.abandonsAfter!} dateOnly /> })}
            </p>
          )}
        </PayUsageChargeForm>
      ) : (
        <p className="text-sm text-neutral-600" data-testid="pay-usage-not-due">
          {t("notDue")} {t(`status.${statusKey}`)}
        </p>
      )}
      <Link href="/account" className="mt-6 inline-block text-sm underline">
        {t("backToAccount")}
      </Link>
    </div>
  );
}
