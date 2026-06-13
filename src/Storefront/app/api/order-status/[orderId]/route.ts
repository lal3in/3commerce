import { NextResponse } from "next/server";
import { getOrderStatus } from "@/lib/gateway";

// Thin proxy so the confirmation page can poll without exposing the gateway URL client-side.
export async function GET(_req: Request, { params }: { params: Promise<{ orderId: string }> }) {
  const { orderId } = await params;
  const status = await getOrderStatus(orderId);
  if (status === null) {
    return NextResponse.json({ error: "not found" }, { status: 404 });
  }
  return NextResponse.json({ status });
}
