import { NextResponse } from "next/server";
import { GATEWAY_URL } from "@/lib/gateway";
import { resolveStorefront } from "@/lib/storefront-context";

// Thin proxy for the consent-gated batcher (lib/analytics.ts, def_4): keeps the gateway URL
// server-side and attributes the batch to the resolved storefront's tenant. Analytics is
// best-effort — any failure is swallowed into a 202 so it can never disrupt the page.
export async function POST(req: Request) {
  try {
    const body = await req.text();
    const storefront = await resolveStorefront();
    const headers: Record<string, string> = { "content-type": "application/json" };
    if (storefront) {
      headers["X-3C-Tenant-Id"] = storefront.tenantId;
    }

    const response = await fetch(`${GATEWAY_URL}/api/marketing/events`, {
      method: "POST",
      headers,
      body,
    });
    if (response.ok) {
      return NextResponse.json(await response.json(), { status: 202 });
    }
  } catch {
    // fall through to the generic accepted-nothing response
  }

  return NextResponse.json({ accepted: 0, rejected: [] }, { status: 202 });
}
