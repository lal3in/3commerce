import { getMyOrder } from "@/lib/gateway";

// The signed-in customer's own order detail, fetched on demand when a row is expanded on the account
// page. Auth is the shopper's session (forwarded server-side by getMyOrder); scoped to their own order.
export async function GET(_req: Request, { params }: { params: Promise<{ orderId: string }> }) {
  const { orderId } = await params;
  const order = await getMyOrder(orderId);
  if (!order) {
    return new Response("Not found", { status: 404 });
  }
  return Response.json(order, { headers: { "cache-control": "no-store" } });
}
