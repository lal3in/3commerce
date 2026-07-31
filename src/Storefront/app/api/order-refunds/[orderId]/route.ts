import { getOrderRefunds } from "@/lib/gateway";

// Client-pollable snapshot of an order's refund/return requests, so their lifecycle status (approved,
// denied, refunded…) updates without a manual refresh. Auth is the shopper's own session (forwarded
// server-side by getOrderRefunds); order-scoped like the underlying endpoint.
export async function GET(_req: Request, { params }: { params: Promise<{ orderId: string }> }) {
  const { orderId } = await params;
  const refunds = await getOrderRefunds(orderId);
  return Response.json(refunds, { headers: { "cache-control": "no-store" } });
}
