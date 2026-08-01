import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { getRefundableOrder, getProfile, getOrderTickets, getOrderRefunds } from "@/lib/gateway";
import { SupportForms } from "@/components/support/SupportForms";
import { TicketHistory } from "@/components/support/TicketHistory";
import { RefundRequests } from "@/components/support/RefundRequests";

export const metadata = { title: "Order support" };

export default async function OrderSupportPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ submitted?: string }>;
}) {
  const t = await getTranslations("support");
  const { id } = await params;
  const { submitted } = await searchParams;
  const [refundable, profile, tickets, refunds] = await Promise.all([
    getRefundableOrder(id),
    getProfile(),
    getOrderTickets(id),
    getOrderRefunds(id),
  ]);
  return (
    <div className="max-w-lg mx-auto space-y-6">
      <Link href="/account" className="inline-flex items-center gap-1 text-sm text-neutral-500 hover:text-neutral-800">
        <span aria-hidden>←</span> {t("backToAccount")}
      </Link>
      <h1 className="text-xl font-semibold">{t("title")}</h1>
      {submitted && (
        <p className="rounded bg-green-50 text-green-700 px-3 py-2 text-sm">{t("submitted")}</p>
      )}
      <RefundRequests orderId={id} refunds={refunds} />
      <TicketHistory orderId={id} tickets={tickets} />
      <SupportForms orderId={id} refundable={refundable} email={profile?.email ?? ""} />
    </div>
  );
}
