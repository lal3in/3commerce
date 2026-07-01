import { accountJsonHeaders, accountStatusRedirect, formText } from "@/lib/account-actions";
import { GATEWAY_URL } from "@/lib/gateway";

function redirectTo(request: Request, status: "saved" | "error") {
  return accountStatusRedirect(request, "card", status);
}

export async function POST(request: Request) {
  const formData = await request.formData();
  const email = formText(formData, "email");
  const providerPaymentMethodId = formText(formData, "providerPaymentMethodId") || tokenForCard(formText(formData, "cardNumber"), formText(formData, "expiry"));

  if (!email.includes("@")) {
    return redirectTo(request, "error");
  }

  const response = await fetch(`${GATEWAY_URL}/api/payments/payment-methods/`, {
    method: "POST",
    headers: await accountJsonHeaders(),
    body: JSON.stringify({
      email,
      providerPaymentMethodId,
      makeDefault: formData.get("makeDefault") === "on",
    }),
  });

  return redirectTo(request, response.ok ? "saved" : "error");
}

function tokenForCard(cardNumber: string, expiry: string) {
  const digits = cardNumber.replace(/\D/g, "");
  const expiryToken = expiry.replace(/\D/g, "").slice(0, 4) || "1229";
  const last4 = digits.slice(-4) || "4242";
  const brand = brandForCard(digits);
  return `pm_card_${brand}_${last4}_${expiryToken}`;
}

function brandForCard(digits: string) {
  if (digits.startsWith("555555")) return "mastercard";
  if (digits.startsWith("34") || digits.startsWith("37")) return "amex";
  return "visa";
}
