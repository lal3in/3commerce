import { accountJsonHeaders, accountStatusRedirect, formText } from "@/lib/account-actions";
import { GATEWAY_URL } from "@/lib/gateway";

export async function POST(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const formData = await request.formData();
  const action = formText(formData, "action");
  const headers = await accountJsonHeaders();

  // mem_2: the card's own billing address (Payments validates it; a refusal is a 400 naming the field).
  if (action === "billing") {
    const response = await fetch(`${GATEWAY_URL}/api/payments/payment-methods/${id}/billing-address`, {
      method: "PUT",
      headers,
      body: JSON.stringify({
        name: formText(formData, "name"),
        line1: formText(formData, "line1"),
        city: formText(formData, "city"),
        region: formText(formData, "region") || null,
        postcode: formText(formData, "postcode"),
        country: formText(formData, "country").toUpperCase(),
      }),
    });
    return accountStatusRedirect(request, "card", response.ok ? "billing-saved" : "billing-error");
  }

  if (action === "clear-billing") {
    const response = await fetch(`${GATEWAY_URL}/api/payments/payment-methods/${id}/billing-address`, {
      method: "DELETE",
      headers,
    });
    return accountStatusRedirect(request, "card", response.ok ? "billing-cleared" : "billing-error");
  }

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
