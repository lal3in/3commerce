import { cookies } from "next/headers";
import { NextResponse } from "next/server";

export async function accountJsonHeaders() {
  const store = await cookies();
  const session = store.get("3c_session");
  const headers: Record<string, string> = { "content-type": "application/json" };
  if (session) {
    headers.cookie = `3c_session=${session.value}`;
  }
  return headers;
}

export function formText(formData: FormData, name: string) {
  return String(formData.get(name) ?? "").trim();
}

export function accountStatusRedirect(request: Request, queryName: "address" | "card", status: "saved" | "updated" | "deleted" | "default" | "error") {
  return NextResponse.redirect(new URL(`/account?${queryName}=${status}`, request.url), { status: 303 });
}
