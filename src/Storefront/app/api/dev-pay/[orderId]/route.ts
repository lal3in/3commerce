import { NextResponse } from "next/server";
import { GATEWAY_URL } from "@/lib/gateway";

// DEV ONLY: completes a simulated payment for the fake provider (no real Stripe key).
// The Payments service itself gates this on its Development environment.
export async function POST(_req: Request, { params }: { params: Promise<{ orderId: string }> }) {
  const { orderId } = await params;
  const intentId = `pi_fake_${orderId.replace(/-/g, "")}`;
  const res = await fetch(`${GATEWAY_URL}/api/payments/dev/simulate-payment/${intentId}`, { method: "POST" });
  return NextResponse.json({ ok: res.ok }, { status: res.ok ? 200 : 502 });
}
