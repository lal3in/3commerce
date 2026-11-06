"use server";

import { revalidatePath } from "next/cache";
import { accountJsonHeaders, formText } from "./account-actions";
import { GATEWAY_URL } from "./gateway";
import type { BusinessIdentityState } from "./business-identity";

// The account's default business identity (ADR-0063 P3b). Identity validates it with checkout's per-scheme rules and
// answers a refusal with 400 problem+json naming the `field`; the member's input is echoed back so nothing is lost.
export async function saveBusinessIdentity(_prev: BusinessIdentityState, formData: FormData): Promise<BusinessIdentityState> {
  const values = {
    companyName: formText(formData, "companyName"),
    taxId: formText(formData, "taxId"),
    taxIdScheme: formText(formData, "taxIdScheme"),
    taxIdJurisdiction: formText(formData, "taxIdJurisdiction"),
  };
  const scheme = Number.parseInt(values.taxIdScheme, 10);
  const response = await fetch(`${GATEWAY_URL}/api/identity/me/business-identity`, {
    method: "PUT",
    headers: await accountJsonHeaders(),
    body: JSON.stringify({
      companyName: values.companyName,
      taxId: values.taxId || null,
      // Enums cross HTTP as NUMBERS (platform invariant).
      taxIdScheme: Number.isFinite(scheme) ? scheme : null,
      taxIdJurisdiction: values.taxIdJurisdiction || null,
    }),
    cache: "no-store",
  });

  if (response.ok) {
    revalidatePath("/account");
    return { status: "saved" };
  }

  let field: string | null = null;
  if (response.status === 400) {
    const problem = (await response.json().catch(() => null)) as { field?: string } | null;
    field = problem?.field ?? null;
  }
  return { status: "error", field, values };
}

// A form action for useActionState: it needs neither the previous state nor the (empty) form.
export async function clearBusinessIdentity(): Promise<BusinessIdentityState> {
  const response = await fetch(`${GATEWAY_URL}/api/identity/me/business-identity`, {
    method: "DELETE",
    headers: await accountJsonHeaders(),
    cache: "no-store",
  });
  if (!response.ok) {
    return { status: "error", field: null };
  }

  revalidatePath("/account");
  return { status: "cleared" };
}
