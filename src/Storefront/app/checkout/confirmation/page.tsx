import { getOrderStatus } from "@/lib/gateway";
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
  return <ConfirmationView orderId={order} initialStatus={initialStatus} />;
}
