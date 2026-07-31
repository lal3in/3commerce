import { getOrderTickets } from "@/lib/gateway";

// Client-pollable snapshot of an order's support tickets, so the customer's thread updates with new
// operator replies without a manual refresh. Auth is the shopper's own session (forwarded server-side
// by getOrderTickets); order-scoped like the underlying endpoint.
export async function GET(_req: Request, { params }: { params: Promise<{ orderId: string }> }) {
  const { orderId } = await params;
  const tickets = await getOrderTickets(orderId);
  return Response.json(tickets, { headers: { "cache-control": "no-store" } });
}
