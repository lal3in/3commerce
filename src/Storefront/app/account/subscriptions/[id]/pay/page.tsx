import Link from "next/link";
import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { getMyOrder, getMySubscription, getProfile, getSavedPaymentMethods } from "@/lib/gateway";
import { formatMoney } from "@/lib/money";
import { LocalTime } from "@/components/LocalTime";
import { PayRenewalForm } from "@/components/account/PayRenewalForm";

export const metadata = { title: "Complete payment" };

// The renewal "complete payment" page (owner decision 2026-10-27, ADR-0057 amendment): linked from the 3-D Secure and
// dunning emails. Shows what the unpaid period owes and lets the customer pay it now — on-session, so the bank may ask
// for 3-D Secure — optionally on another saved card. Dynamic, cookie-dependent — never cached (components.md §1).
export default async function PayRenewalPage({ params }: { params: Promise<{ id: string }> }) {
  const [t, profile] = await Promise.all([getTranslations("subscriptions"), getProfile()]);
  if (!profile) {
    redirect("/login");
  }

  const { id } = await params;
  const subscription = await getMySubscription(id);
  if (!subscription) {
    return (
      <div className="max-w-xl">
        <h1 className="text-xl font-semibold mb-4">{t("pageTitle")}</h1>
        <p className="text-sm text-neutral-600">{t("notFound")}</p>
        <Link href="/account" className="mt-4 inline-block text-sm underline">{t("backToAccount")}</Link>
      </div>
    );
  }

  const [order, cards] = await Promise.all([getMyOrder(subscription.orderId), getSavedPaymentMethods()]);
  const title = order?.lines.find((l) => l.productId === subscription.productId)?.title ?? t("item");
  const pastDue = subscription.status === "PastDue";
  const amountDue = subscription.amountDueMinor ?? subscription.priceMinor;
  const reasonKey = (["RequiresAction", "Failed", "Cancelled", "Error"] as const).find((r) => r === subscription.failureReason);

  return (
    <div className="max-w-xl" data-testid="pay-renewal">
      <h1 className="text-xl font-semibold mb-2">{t("pageTitle")}</h1>
      {pastDue ? (
        <>
          <p className="mb-4 text-sm text-neutral-600">{t("intro")}</p>
          {reasonKey && (
            <p className="mb-4 rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800" data-testid="pay-renewal-reason">
              {t(`reason.${reasonKey}`)}
            </p>
          )}
          <dl className="space-y-2 text-sm">
            <div className="flex justify-between border-b border-neutral-100 py-2">
              <dt className="text-neutral-500">{t("item")}</dt>
              <dd>{subscription.quantity > 1 ? `${title} × ${subscription.quantity}` : title}</dd>
            </div>
            <div className="flex justify-between border-b border-neutral-100 py-2">
              <dt className="text-neutral-500">{t("period")}</dt>
              <dd><LocalTime iso={subscription.currentPeriodEnd} dateOnly /></dd>
            </div>
            <div className="flex justify-between border-b border-neutral-100 py-2 font-medium">
              <dt>{t("amountDue")}</dt>
              <dd data-testid="pay-renewal-amount">{formatMoney(amountDue, subscription.currency)}</dd>
            </div>
          </dl>
          {subscription.nextRetryAt && (
            <p className="mt-3 text-sm text-neutral-600">
              {t.rich("nextRetry", { date: () => <LocalTime iso={subscription.nextRetryAt!} dateOnly /> })}
            </p>
          )}
          {subscription.cancelsAfter && (
            <p className="mt-1 text-sm text-neutral-600">
              {t.rich("cancelsAfter", { date: () => <LocalTime iso={subscription.cancelsAfter!} dateOnly /> })}
            </p>
          )}
          <PayRenewalForm
            subscriptionId={subscription.id}
            idempotencyKey={crypto.randomUUID()}
            amountLabel={formatMoney(amountDue, subscription.currency)}
            cards={cards.map((c) => ({ id: c.id, label: t("savedCard", { brand: c.brand.toUpperCase(), last4: c.last4 }) }))}
          />
        </>
      ) : (
        <p className="text-sm text-neutral-600" data-testid="pay-renewal-not-due">
          {t("notDue")} {t(`status.${(["Active", "Trialing", "PastDue", "Cancelled"] as const).find((s) => s === subscription.status) ?? "Active"}`)}
        </p>
      )}
      <Link href="/account" className="mt-6 inline-block text-sm underline">{t("backToAccount")}</Link>
    </div>
  );
}
