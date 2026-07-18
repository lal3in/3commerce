"use server";

import { cookies } from "next/headers";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { GATEWAY_URL } from "./gateway";
import { resolveStorefront } from "./storefront-context";

async function cartHeaders(): Promise<HeadersInit> {
  const store = await cookies();
  const cart = store.get("3c_cart");
  const session = store.get("3c_session");
  const headers: Record<string, string> = { "content-type": "application/json" };
  const cookieParts: string[] = [];
  if (cart) cookieParts.push(`3c_cart=${cart.value}`);
  if (session) cookieParts.push(`3c_session=${session.value}`);
  if (cookieParts.length) headers["cookie"] = cookieParts.join("; ");
  return headers;
}

async function relayCartCookie(response: Response): Promise<void> {
  const match = response.headers.get("set-cookie")?.match(/3c_cart=([^;]+)/);
  if (match) {
    const store = await cookies();
    store.set("3c_cart", match[1], { httpOnly: true, sameSite: "lax", path: "/" });
  }
}

export async function addToCart(productId: string, variantId?: string, quantity = 1, currency?: string): Promise<void> {
  const response = await fetch(`${GATEWAY_URL}/api/ordering/cart/items`, {
    method: "POST",
    headers: await cartHeaders(),
    body: JSON.stringify({ productId, variantId, quantity: Math.max(1, quantity), currency }),
  });
  await relayCartCookie(response);
  revalidatePath("/cart");
}

export async function updateCartQuantity(productId: string, variantId: string | null, quantity: number): Promise<void> {
  const suffix = variantId ? `${productId}/${variantId}` : productId;
  await fetch(`${GATEWAY_URL}/api/ordering/cart/items/${suffix}`, {
    method: "PUT",
    headers: await cartHeaders(),
    body: JSON.stringify({ quantity }),
  });
  revalidatePath("/cart");
  revalidatePath("/checkout");
}

export async function removeFromCart(productId: string, variantId?: string | null): Promise<void> {
  const suffix = variantId ? `${productId}/${variantId}` : productId;
  await fetch(`${GATEWAY_URL}/api/ordering/cart/items/${suffix}`, {
    method: "DELETE",
    headers: await cartHeaders(),
  });
  revalidatePath("/cart");
  revalidatePath("/checkout");
}

export type CheckoutState = { error?: string };
export type ShippingRate = { carrier: string; service: string; serviceName: string; amountMinor: number; currency: string; estimatedDays: number; expiresAt: string };

export async function quoteCheckoutShipping(formData: FormData): Promise<{ rates?: ShippingRate[]; error?: string }> {
  const destination = {
    name: String(formData.get("shippingName") || "Checkout"),
    line1: String(formData.get("shippingLine1") || ""),
    city: String(formData.get("shippingCity") || ""),
    postcode: String(formData.get("shippingPostcode") || ""),
    country: String(formData.get("shippingCountry") || ""),
  };
  if (!destination.line1 || !destination.city || !destination.postcode || destination.country.length !== 2) {
    return { error: "Enter a complete shipping address before getting rates." };
  }

  const response = await fetch(`${GATEWAY_URL}/api/fulfillment/shipping/quote`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      destination,
      origin: { name: "3commerce warehouse", line1: "1 Warehouse Way", city: "Sydney", postcode: "2000", country: "AU" },
      parcel: { weightGrams: 500, lengthMm: 200, widthMm: 150, heightMm: 100 },
    }),
    cache: "no-store",
  });
  if (!response.ok) return { error: "Could not retrieve shipping rates. Try again." };
  const body = (await response.json()) as { rates: Omit<ShippingRate, "expiresAt">[]; expiresAt: string };
  return { rates: body.rates.map((rate) => ({ ...rate, expiresAt: body.expiresAt })) };
}

export async function submitCheckout(_prev: CheckoutState, formData: FormData): Promise<CheckoutState> {
  const headers = await cartHeaders();
  const profileResponse = await fetch(`${GATEWAY_URL}/api/identity/me`, { headers, cache: "no-store" });
  const profile = profileResponse.ok ? ((await profileResponse.json()) as { email: string }) : null;
  const email = profile?.email ?? String(formData.get("email") ?? "");
  const savedPaymentMethodId = String(formData.get("savedPaymentMethodId") ?? "");
  const paymentOption = String(formData.get("paymentOption") || "CreditCard");
  const cardNumber = String(formData.get("paymentCardNumber") || "").replace(/\D/g, "");
  const body = {
    email,
    savedPaymentMethodId: savedPaymentMethodId && savedPaymentMethodId !== "new" ? savedPaymentMethodId : null,
    savePaymentMethod: Boolean(profile) && formData.get("savePaymentMethod") === "on",
    paymentOption,
    paymentInstrumentSummary: paymentSummary(paymentOption, cardNumber),
    shippingAddress: {
      name: String(formData.get("shippingName") || formData.get("name") || ""),
      line1: String(formData.get("shippingLine1") || formData.get("line1") || ""),
      city: String(formData.get("shippingCity") || formData.get("city") || ""),
      region: String(formData.get("shippingRegion") || formData.get("region") || "") || null,
      postcode: String(formData.get("shippingPostcode") || formData.get("postcode") || ""),
      country: String(formData.get("shippingCountry") || formData.get("country") || ""),
    },
    selectedShippingService: String(formData.get("selectedShippingService") || "") || null,
    selectedShippingAmountMinor: Number(formData.get("selectedShippingAmountMinor") || "") || null,
    selectedShippingExpiresAt: String(formData.get("selectedShippingExpiresAt") || "") || null,
  };
  // Attribute the order to the active storefront (rev_5): Ordering reads these headers into
  // CheckoutAttempt.TenantId/StorefrontId; without them it falls back to the default tenant.
  const storefront = await resolveStorefront();
  const checkoutHeaders: Record<string, string> = { ...(headers as Record<string, string>) };
  if (storefront) {
    checkoutHeaders["X-3C-Tenant-Id"] = storefront.tenantId;
    checkoutHeaders["X-3C-Storefront-Id"] = storefront.id;
  }

  const response = await fetch(`${GATEWAY_URL}/api/ordering/checkout`, {
    method: "POST",
    headers: checkoutHeaders,
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    return { error: "Checkout failed. Please review your cart and details." };
  }
  const result = (await response.json()) as { orderId: string };
  // Remember only guest email so the confirmation page can offer account creation (FR-7).
  const cookieStore = await cookies();
  if (profile) {
    cookieStore.delete("3c_guest_email");
    cookieStore.delete("3c_guest_details");
  } else {
    cookieStore.set("3c_guest_email", body.email, { httpOnly: true, sameSite: "lax", path: "/", maxAge: 3600 });
    // Stash what the guest just typed so the post-checkout account offer isn't an empty form (mem_1).
    const details = JSON.stringify({ name: body.shippingAddress.name, phone: String(formData.get("phone") ?? "") });
    cookieStore.set("3c_guest_details", details, { httpOnly: true, sameSite: "lax", path: "/", maxAge: 3600 });
  }
  redirect(`/checkout/confirmation?order=${result.orderId}`);
}

function paymentSummary(paymentOption: string, cardNumber: string) {
  if (paymentOption === "CreditCard" && cardNumber.length >= 4) {
    return `Credit card ending ${cardNumber.slice(-4)}`;
  }
  return paymentOption;
}

export type ConvertState = { error?: string; ok?: boolean };

// FR-7: post-purchase guest -> account. Registers with the checkout email; once the
// email is verified, the guest order attaches to the new account's order history.
export async function convertGuest(_prev: ConvertState, formData: FormData): Promise<ConvertState> {
  const firstName = String(formData.get("firstName") ?? "").trim();
  const lastName = String(formData.get("lastName") ?? "").trim();
  const phone = String(formData.get("phone") ?? "").trim();
  const dateOfBirth = String(formData.get("dateOfBirth") ?? "").trim();
  if (!firstName || !lastName || !phone || !dateOfBirth) {
    return { error: "First name, last name, phone and date of birth are required." };
  }

  const opt = (k: string) => {
    const v = String(formData.get(k) ?? "").trim();
    return v.length > 0 ? v : null;
  };
  const response = await fetch(`${GATEWAY_URL}/api/identity/convert-guest`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      email: String(formData.get("email") ?? ""),
      password: String(formData.get("password") ?? ""),
      title: opt("title"),
      firstName,
      middleName: opt("middleName"),
      lastName,
      preferredName: opt("preferredName"),
      phone,
      dateOfBirth,
      marketingConsent: formData.get("marketingConsent") === "on",
    }),
  });
  if (!response.ok) {
    return { error: "Could not create the account. Try a longer password." };
  }
  const cookieStore = await cookies();
  cookieStore.delete("3c_guest_email");
  cookieStore.delete("3c_guest_details");
  return { ok: true };
}
