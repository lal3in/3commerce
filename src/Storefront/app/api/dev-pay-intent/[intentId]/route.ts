import { NextResponse } from "next/server";
import { GATEWAY_URL } from "@/lib/gateway";

// DEV ONLY: completes a simulated 3-D Secure authentication for a subscription renewal attempt or a usage charge attempt
// (usage_charge_pay_page) on the fake provider (no real Stripe key) — the provider's success event then settles the
// period / charge. The Payments service itself gates this on its Development environment, exactly like /api/dev-pay for
// a checkout.
export async function POST(_req: Request, { params }: { params: Promise<{ intentId: string }> }) {
  const { intentId } = await params;
  if (!/^pi_fake_(renew|charge)_[0-9a-f]+$/.test(intentId)) {
    return NextResponse.json({ ok: false }, { status: 400 });
  }
  const res = await fetch(`${GATEWAY_URL}/api/payments/dev/simulate-payment/${intentId}`, { method: "POST" });
  return NextResponse.json({ ok: res.ok }, { status: res.ok ? 200 : 502 });
}
