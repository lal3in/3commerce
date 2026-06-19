import { getRefundableOrder } from "@/lib/gateway";
import { SupportForms } from "@/components/support/SupportForms";

export const metadata = { title: "Order support" };

export default async function OrderSupportPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ submitted?: string }>;
}) {
  const { id } = await params;
  const { submitted } = await searchParams;
  const refundable = await getRefundableOrder(id);
  return (
    <div className="max-w-lg mx-auto space-y-6">
      <h1 className="text-xl font-semibold">Order support</h1>
      {submitted && (
        <p className="rounded bg-green-50 text-green-700 px-3 py-2 text-sm">
          Your request was submitted. We&apos;ll email you with updates.
        </p>
      )}
      <SupportForms orderId={id} refundable={refundable} />
    </div>
  );
}
