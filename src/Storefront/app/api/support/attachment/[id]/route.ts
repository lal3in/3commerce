import { cookies } from "next/headers";
import { GATEWAY_URL } from "@/lib/gateway";

// Same-origin proxy so the browser can download a support attachment: it forwards the shopper's
// session cookie to the gateway (which the browser can't reach directly with auth) and streams the
// file back. Keeps the gateway off the public surface.
export async function GET(_req: Request, { params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const store = await cookies();
  const session = store.get("3c_session");
  const res = await fetch(`${GATEWAY_URL}/api/support/attachments/${id}`, {
    headers: session ? { cookie: `3c_session=${session.value}` } : {},
    cache: "no-store",
  });
  if (!res.ok || !res.body) {
    return new Response("Not found", { status: res.status === 200 ? 404 : res.status });
  }
  return new Response(res.body, {
    headers: {
      "content-type": res.headers.get("content-type") ?? "application/octet-stream",
      "content-disposition": res.headers.get("content-disposition") ?? "attachment",
    },
  });
}
