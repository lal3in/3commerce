"use server";

import { cookies } from "next/headers";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { GATEWAY_URL } from "./gateway";

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

export async function addToCart(productId: string): Promise<void> {
  const response = await fetch(`${GATEWAY_URL}/api/ordering/cart/items`, {
    method: "POST",
    headers: await cartHeaders(),
    body: JSON.stringify({ productId, quantity: 1 }),
  });
  await relayCartCookie(response);
  revalidatePath("/cart");
}

export async function removeFromCart(productId: string): Promise<void> {
  await fetch(`${GATEWAY_URL}/api/ordering/cart/items/${productId}`, {
    method: "DELETE",
    headers: await cartHeaders(),
  });
  revalidatePath("/cart");
}

export type CheckoutState = { error?: string };

export async function submitCheckout(_prev: CheckoutState, formData: FormData): Promise<CheckoutState> {
  const body = {
    email: String(formData.get("email") ?? ""),
    shippingAddress: {
      name: String(formData.get("name") ?? ""),
      line1: String(formData.get("line1") ?? ""),
      city: String(formData.get("city") ?? ""),
      postcode: String(formData.get("postcode") ?? ""),
      country: String(formData.get("country") ?? ""),
    },
  };
  const response = await fetch(`${GATEWAY_URL}/api/ordering/checkout`, {
    method: "POST",
    headers: await cartHeaders(),
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    return { error: "Checkout failed. Please review your cart and details." };
  }
  const result = (await response.json()) as { orderId: string };
  // Remember the guest email so the confirmation page can offer account creation (FR-7).
  (await cookies()).set("3c_guest_email", body.email, { httpOnly: true, sameSite: "lax", path: "/", maxAge: 3600 });
  redirect(`/checkout/confirmation?order=${result.orderId}`);
}

export type ConvertState = { error?: string; ok?: boolean };

// FR-7: post-purchase guest -> account. Registers with the checkout email; once the
// email is verified, the guest order attaches to the new account's order history.
export async function convertGuest(_prev: ConvertState, formData: FormData): Promise<ConvertState> {
  const response = await fetch(`${GATEWAY_URL}/api/identity/convert-guest`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      email: String(formData.get("email") ?? ""),
      password: String(formData.get("password") ?? ""),
    }),
  });
  if (!response.ok) {
    return { error: "Could not create the account. Try a longer password." };
  }
  (await cookies()).delete("3c_guest_email");
  return { ok: true };
}
