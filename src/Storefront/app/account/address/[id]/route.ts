import { accountJsonHeaders, accountStatusRedirect, formText } from "@/lib/account-actions";
import { GATEWAY_URL } from "@/lib/gateway";

function addressPurpose(value: string) {
  if (value === "Billing") return 1;
  if (value === "Shipping") return 2;
  return 3;
}

export async function POST(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const formData = await request.formData();
  const action = formText(formData, "action");
  const headers = await accountJsonHeaders();

  if (action === "delete") {
    const response = await fetch(`${GATEWAY_URL}/api/identity/me/addresses/${id}`, {
      method: "DELETE",
      headers,
    });
    return accountStatusRedirect(request, "address", response.ok ? "deleted" : "error");
  }

  const body = {
    purpose: addressPurpose(formText(formData, "purpose")),
    isDefault: formData.get("isDefault") === "on",
    name: formText(formData, "name"),
    line1: formText(formData, "line1"),
    line2: formText(formData, "line2") || null,
    city: formText(formData, "city"),
    region: formText(formData, "region") || null,
    postcode: formText(formData, "postcode"),
    country: formText(formData, "country").toUpperCase(),
  };

  if (!body.name || !body.line1 || !body.city || !body.postcode || body.country.length !== 2) {
    return accountStatusRedirect(request, "address", "error");
  }

  const response = await fetch(`${GATEWAY_URL}/api/identity/me/addresses/${id}`, {
    method: "PUT",
    headers,
    body: JSON.stringify(body),
  });
  return accountStatusRedirect(request, "address", response.ok ? "updated" : "error");
}
