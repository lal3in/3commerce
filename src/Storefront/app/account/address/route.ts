import { accountJsonHeaders, accountStatusRedirect, formText } from "@/lib/account-actions";
import { GATEWAY_URL } from "@/lib/gateway";

function redirectTo(request: Request, status: "saved" | "error") {
  return accountStatusRedirect(request, "address", status);
}

function addressPurpose(value: string) {
  if (value === "Billing") return 1;
  if (value === "Shipping") return 2;
  return 3;
}

export async function POST(request: Request) {
  const formData = await request.formData();
  const body = {
    purpose: addressPurpose(formText(formData, "purpose")),
    isDefault: formData.get("isDefault") === "on",
    name: formText(formData, "name"),
    line1: formText(formData, "line1"),
    line2: formText(formData, "line2") || null,
    city: formText(formData, "city"),
    postcode: formText(formData, "postcode"),
    country: formText(formData, "country").toUpperCase(),
  };

  if (!body.name || !body.line1 || !body.city || !body.postcode || body.country.length !== 2) {
    return redirectTo(request, "error");
  }

  const response = await fetch(`${GATEWAY_URL}/api/identity/me/addresses`, {
    method: "POST",
    headers: await accountJsonHeaders(),
    body: JSON.stringify(body),
  });

  return redirectTo(request, response.ok ? "saved" : "error");
}
