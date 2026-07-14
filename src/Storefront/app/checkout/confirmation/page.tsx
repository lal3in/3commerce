import { cookies } from "next/headers";
import { getTranslations } from "next-intl/server";
import { getOrderStatus, getProfile } from "@/lib/gateway";
import { ConfirmationView } from "@/components/checkout/ConfirmationView";

export const metadata = { title: "Order confirmation" };

export default async function ConfirmationPage({
  searchParams,
}: {
  searchParams: Promise<{ order?: string }>;
}) {
  const { order } = await searchParams;
  if (!order) {
    const t = await getTranslations("confirmation");
    return <p className="text-center py-16">{t("noOrder")}</p>;
  }

  const initialStatus = (await getOrderStatus(order)) ?? "AwaitingPayment";
  const profile = await getProfile();
  const jar = await cookies();
  const guestEmail = jar.get("3c_guest_email")?.value ?? "";
  // Pre-fill the account offer with what the guest typed at checkout (mem_1) — no more empty form.
  let guestName = "";
  let guestPhone = "";
  try {
    const d = JSON.parse(jar.get("3c_guest_details")?.value ?? "{}") as { name?: string; phone?: string };
    guestName = d.name ?? "";
    guestPhone = d.phone ?? "";
  } catch {
    /* no details stashed */
  }
  return (
    <ConfirmationView
      orderId={order}
      initialStatus={initialStatus}
      guestEmail={guestEmail}
      guestName={guestName}
      guestPhone={guestPhone}
      isAuthenticated={Boolean(profile)}
    />
  );
}
