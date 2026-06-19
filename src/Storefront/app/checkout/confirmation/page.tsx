import { cookies } from "next/headers";
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
    return <p className="text-center py-16">No order specified.</p>;
  }

  const initialStatus = (await getOrderStatus(order)) ?? "AwaitingPayment";
  const profile = await getProfile();
  const guestEmail = (await cookies()).get("3c_guest_email")?.value ?? "";
  return <ConfirmationView orderId={order} initialStatus={initialStatus} guestEmail={guestEmail} isAuthenticated={Boolean(profile)} />;
}
