import type { APIRequestContext } from "@playwright/test";

// Approval-gated availability (DECISION A): an offer only counts when its supplier is APPROVED (the Entity
// onboarding reached Active). These helpers drive the real Entity API — the same path the admin uses — so
// e2e specs can create an approved supplier to back an offer, and revoke approval to prove the gate.

const TENANT = "00000000-0000-0000-0000-000000000001";

async function id(response: { json(): Promise<unknown> }): Promise<string> {
  return ((await response.json()) as { id: string }).id;
}

/** Provisions a brand-new supplier and drives onboarding all the way to Active (approved). Returns its entity id. */
export async function provisionApprovedSupplier(request: APIRequestContext, gateway: string): Promise<string | null> {
  const create = await request.post(`${gateway}/api/entity/entities`, {
    data: { tenantId: TENANT, type: 2, legalName: `E2E Supplier ${crypto.randomUUID()}`, tradingName: "E2E Supplier", roles: [2] },
  });
  if (!create.ok()) return null;
  const entityId = await id(create);

  await request.post(`${gateway}/api/entity/entities/${entityId}/suppliers`);

  // Readiness data: a verified ABN, an operations email, and a current warehouse address.
  const ident = await request.post(`${gateway}/api/entity/entities/${entityId}/identifiers`, {
    data: { type: 1, value: "12345678901" },
  });
  if (ident.ok()) {
    const detail = (await ident.json()) as { identifiers: { id: string }[] };
    const identifierId = detail.identifiers[0]?.id;
    if (identifierId) {
      await request.post(`${gateway}/api/entity/entities/${entityId}/identifiers/${identifierId}/verify`);
    }
  }
  await request.post(`${gateway}/api/entity/entities/${entityId}/contacts`, {
    data: { purpose: 3, kind: 1, value: "ops@e2e-supplier.test" },
  });
  await request.post(`${gateway}/api/entity/entities/${entityId}/addresses`, {
    data: { purpose: 4, line1: "1 Supplier Way", line2: null, city: "Sydney", region: "NSW", postcode: "2000", countryCode: "AU" },
  });

  await request.post(`${gateway}/api/entity/entities/${entityId}/suppliers/submit-verification`);
  await request.post(`${gateway}/api/entity/entities/${entityId}/suppliers/verification-complete`);
  const activate = await request.post(`${gateway}/api/entity/entities/${entityId}/suppliers/activate`);
  if (!activate.ok()) return null;
  return entityId;
}

/** Revokes approval by suspending the active supplier (publishes SupplierApprovalChanged(false)). */
export async function suspendSupplier(request: APIRequestContext, gateway: string, entityId: string): Promise<boolean> {
  const r = await request.post(`${gateway}/api/entity/entities/${entityId}/suppliers/suspend`, {
    data: { reason: "E2E approval-gate check" },
  });
  return r.ok();
}
