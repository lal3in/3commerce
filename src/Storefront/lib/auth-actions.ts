"use server";

import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { GATEWAY_URL } from "./gateway";

export type AuthState = { error?: string; mfaRequired?: boolean };

// Mutations go through Server Actions so cookies/internal URLs never reach the client
// (components.md §2). On login we forward the gateway's Set-Cookie to the browser.
export async function login(_prev: AuthState, formData: FormData): Promise<AuthState> {
  const email = String(formData.get("email") ?? "");
  const password = String(formData.get("password") ?? "");
  const mfaCode = String(formData.get("mfaCode") ?? "").trim();

  const response = await fetch(`${GATEWAY_URL}/api/identity/login`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ email, password }),
  });

  if (!response.ok) {
    return { error: "Invalid email or password." };
  }

  // MFA-enrolled account: the session is pending until /mfa/challenge passes; a pending
  // session carries no claims anywhere, so completing it here is the only option.
  const body = (await response.json().catch(() => null)) as { mfaRequired?: boolean } | null;
  if (body?.mfaRequired) {
    const token = response.headers.get("set-cookie")?.match(/3c_session=([^;]+)/)?.[1];
    if (!mfaCode || !token) {
      return { mfaRequired: true, error: "Enter the 6-digit code from your authenticator app." };
    }

    const challenge = await fetch(`${GATEWAY_URL}/api/identity/mfa/challenge`, {
      method: "POST",
      headers: { "content-type": "application/json", cookie: `3c_session=${token}` },
      body: JSON.stringify({ code: mfaCode }),
    });
    if (!challenge.ok) {
      return { mfaRequired: true, error: "That code was not accepted — try the current one." };
    }
  }

  await forwardSessionCookie(response);
  redirect("/account");
}

export async function register(_prev: AuthState, formData: FormData): Promise<AuthState> {
  const email = String(formData.get("email") ?? "");
  const password = String(formData.get("password") ?? "");

  const response = await fetch(`${GATEWAY_URL}/api/identity/register`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ email, password }),
  });

  if (!response.ok) {
    return { error: "Could not register. Try a different email or a longer password." };
  }

  redirect("/login?registered=1");
}

export async function logout(): Promise<void> {
  const cookieStore = await cookies();
  const session = cookieStore.get("3c_session");
  if (session) {
    await fetch(`${GATEWAY_URL}/api/identity/logout`, {
      method: "POST",
      headers: { cookie: `3c_session=${session.value}` },
    });
    cookieStore.delete("3c_session");
  }
  redirect("/");
}

async function forwardSessionCookie(response: Response): Promise<void> {
  const setCookie = response.headers.get("set-cookie");
  const match = setCookie?.match(/3c_session=([^;]+)/);
  if (match) {
    const cookieStore = await cookies();
    cookieStore.set("3c_session", match[1], {
      httpOnly: true,
      sameSite: "lax",
      path: "/",
    });
  }
}
