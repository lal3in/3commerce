import { accountJsonHeaders, accountStatusRedirect, formText } from "@/lib/account-actions";
import { GATEWAY_URL } from "@/lib/gateway";

export async function POST(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const action = formText(await request.formData(), "action");
  const headers = await accountJsonHeaders();

  if (action === "make-default") {
    const response = await fetch(`${GATEWAY_URL}/api/payments/payment-methods/${id}/default`, {
      method: "POST",
      headers,
    });
    return accountStatusRedirect(request, "card", response.ok ? "default" : "error");
  }

  const listResponse = await fetch(`${GATEWAY_URL}/api/payments/payment-methods/`, { headers, cache: "no-store" });
  if (!listResponse.ok) {
    return accountStatusRedirect(request, "card", "error");
  }
  const methods = (await listResponse.json()) as Array<{ id: string; isDefault: boolean }>;
  const method = methods.find((candidate) => candidate.id === id);
  if (!method || method.isDefault || methods.length <= 1) {
    return accountStatusRedirect(request, "card", "error");
  }

  const response = await fetch(`${GATEWAY_URL}/api/payments/payment-methods/${id}`, {
    method: "DELETE",
    headers,
  });
  return accountStatusRedirect(request, "card", response.ok ? "deleted" : "error");
}
