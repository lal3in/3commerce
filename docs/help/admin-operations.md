# Admin operations

The internal operator console — a **Blazor Server** app at
**`http://localhost:5200`** (ADR-0019). It holds no service references and no
database access: every screen calls the YARP gateway through `GatewayClient`,
forwarding the operator's `3c_session` cookie. The **`admin` role is required** on
all pages.

Strategic communicator note: the Admin console is the proof surface for the platform's
operator-grade story — controlled supplier/entity setup, RBAC, offers, payments,
payouts, Xero mappings, RMAs/refunds, ledger visibility, and mission control. See
[Selling information](./selling-information.md) for external positioning and claim boundaries.

## Network posture & auth pipeline

`Program.cs` wires the request pipeline as: **IP allowlist → static files →
antiforgery → authentication → authorization**.

- **IP allowlist** (`Services/IpAllowlistMiddleware.cs`): runs before auth. Reads
  `Admin:AllowedIPs` (comma-separated IPs/CIDRs). **Empty = allow all** (local dev
  default). Non-matching IPs get `403 Forbidden`.
- **Cookie auth**: the admin app keeps its **own** auth cookie; the gateway session
  token is stored inside it as a `3c_session` claim and forwarded to the gateway on
  every call.

---

## New operator surfaces (platform expansion)

> In progress on `feat/mt-phase1-foundation`. These nav items exist alongside the
> classic MVP screens below. They are tenant-scoped — most take a **Tenant ID**
> field (the seeded default is `00000000-0000-0000-0000-000000000001`).

- **Entities & suppliers** (`/entities`, `Components/Pages/Entities.razor`) — list
  and create tenant party records (companies, people, trusts…), start supplier
  onboarding, and work the **supplier change-request approval queue** (approve /
  reject with maker-checker: the deciding operator must differ from the requester,
  and a reason is required to reject). Backed by the **Entity** service
  (`/api/entity/...`).
- **Roles & permissions** (`/rbac`, `Components/Pages/Rbac.razor`) — the dynamic
  RBAC console: view the code-defined permission registry and the tenant's roles
  (seeded catalog: admin/ops/finance/support/merchandiser/customer), editable and
  clonable. Changes are enforced by the Identity/Authz **PDP** and re-evaluate
  active sessions (ADR-0025).
- **Commerce ops** (`/commerce-ops`, `Components/Pages/CommerceOps.razor`) —
  storefront lifecycle, domains, and product-publication operations, plus links into
  the now-actionable payment/pricing setup pages.
- **Offers & pricing** (`/offers`, `Components/Pages/Offers.razor`) — Catalog Offer
  CRUD for `(product/variant × supplier)` supply profiles: supply category,
  fulfilment type, price, pricing model, billing period, tiers, priority, and active
  state.
- **Payment accounts** (`/payment-accounts`, `Components/Pages/PaymentAccounts.razor`) —
  Payments-owned tenant/storefront payment accounts: create Draft accounts, submit,
  readiness-gated activate, suspend, and archive.
- **Supplier payouts** (`/supplier-payouts`, `Components/Pages/SupplierPayouts.razor`) —
  tokenized/masked supplier bank account setup (approve/reject/archive) and active
  payout instructions. Raw bank details are never entered; only vault token refs and
  masked display values are stored.
- **Xero mappings** (`/xero-mappings`, `Components/Pages/XeroMappings.razor`) —
  ledger-account to Xero-account mapping CRUD with tenant-default, storefront,
  category, supplier, and product override precedence.
- **Supplier Portal** (separate app, `:5300`) — suppliers sign in to view
  readiness, upload stock feeds, and raise user/contact/bank **change requests**
  that operators approve here.
- **CLI** (`src/Cli`, `dotnet tool`) — the same operations from a terminal,
  Gateway-only, with explicit `--tenant`/`--storefront` scope (command surfaces are
  scaffolded; live calls follow once CLI auth lands).

### Digital supply & billing (Phase 7)

Composable supply (ADR-0028) lets one product be sold physical, digital, subscription,
or metered — the **Offer** carries the price model, and confirmation issues the right
artifact. These operator surfaces are HTTP today (admin app screens follow); see
`docs/api/api_contracts_index.md` for the full contracts.

- **Price models on offers** — the `/offers` admin page uses `POST/PUT
  /api/catalog/admin/offers[/{id}]` to create/update supply profiles and set
  `pricing_model` (`OneTime`/`Subscription`/`UsageBased`/`Tiered`), `billing_period`
  (`Once`/`Monthly`/`Yearly`), and graduated `tiers` (`from_quantity` →
  `unit_price`). `PriceFor(qty)` rates flat or per-tier-block; subscriptions price
  per period.
- **Entitlements** (`GET /api/fulfillment/admin/entitlements`) — a confirmed digital/service
  line issues an `Entitlement` (Subscription/License/Download/ApiAccess/ServiceAccess)
  **instead of a shipment**. Filter by tenant / order / email.
- **Subscriptions** (`GET /api/payments/admin/subscriptions`, `POST …/{id}/renew|cancel`) —
  a recurring line sets one up on confirm (first period paid with the order). **Renew**
  charges the period via the payment rail and advances; a failed charge → **PastDue**
  (dunning). **Cancel** ends it.
- **Usage metering** (`POST /api/fulfillment/admin/usage/provision|record`,
  `…/balances/{id}/bill-overage`, `GET …/balances`) — **provision** an allowance + overage
  price, **record** append-only usage (the balance is kept incrementally; access is **gated**
  when overage is off and the allowance is spent), then **bill-overage** to charge the
  unbilled overage via the rail (idempotent — re-billing without new usage is a no-op).

---

## 1. Login — `/login`

File: `Components/Pages/Login.razor`; handler `Services/LoginEndpoints.cs`.
Uses the `EmptyLayout` (no nav). Login is a plain HTML form POST with an
antiforgery token.

Steps:

1. Go to `http://localhost:5200`. Unauthenticated requests redirect to `/login`.
2. Enter the seeded dev admin credentials:
   - **Email:** `admin@3commerce.local`
   - **Password:** `dev-admin-password-1`
3. Click **Sign in** → posts to `/auth/login`.

What `/auth/login` does:

1. Reads `email`/`password` from the form and `POST`s `/api/identity/login`
   through the gateway.
2. Extracts the `3c_session` token from the gateway's `Set-Cookie`. If login
   failed or no token → redirect `/login?error=1`.
3. **Verifies the admin role** by probing an admin-only endpoint
   (`GET /api/payments/admin/xero/sync-runs`) with that session cookie. If the
   probe is not successful → redirect `/login?error=2` (not an admin).
4. On success it signs the operator into the **local** cookie with claims
   `Name=<email>`, `Role=admin`, and the `3c_session` token, then redirects to `/`.

On error the login page shows: "Invalid credentials or not an admin account."
**Log out** (the button in the main nav) posts to `/auth/logout`, signs out of the
local cookie, and returns to `/login`.

---

## 2. Dashboard — `/`

File: `Components/Pages/Home.razor`. `[Authorize(Roles = "admin")]`. A landing page:
"Welcome to the 3commerce operator console. Use the navigation to manage orders,
RMAs, the ledger, and Xero sync." The left nav (`MainLayout.razor`) links to
Dashboard, Catalog, Offers & pricing, Orders, RMA queue, Ledger, Xero sync,
Xero mappings, Imports, Roles & permissions, Operator users, Entities & suppliers,
Commerce ops, Payment accounts, Supplier payouts, Mission control, plus **Log out**.

---

## 3. Orders — `/orders`

File: `Components/Pages/Orders.razor`. Lists orders from
`GET /api/ordering/admin/orders` with **Order**, **Status**, **Total**, **Placed**,
and actions. Operators can cancel unpaid/pending orders via
`POST /api/ordering/admin/orders/<id>/cancel`; confirmed orders show a **Refund**
action that calls the Payments admin refund path, preserving the single refund
saga/ledger reversal.

---

## 4. RMA queue — `/rmas` (operator approve → refund)

File: `Components/Pages/RmaQueue.razor`. `InteractiveServer` render mode. This is
the key operator money flow.

On load it calls `GatewayClient.GetListAsync(...)` →
`GET /api/support/admin/rmas` and renders a table: **Order**, **Amount**
(`AmountMinor / 100`), **State**, **Reason**, and actions.

### Approve → refund

1. Open `/rmas`. Find the row for the customer's order — state **Requested**.
2. Click **Approve & refund**. (All buttons are disabled while the call runs.)
3. The page `POST`s
   `/api/support/admin/rmas/<id>/approve` with body `{ requireReturn: false }` and
   an `Idempotency-Key: approve-<id>` header.
4. This triggers the refund saga on the backend:
   **RefundRequested → refund executes → RefundIssued**. The refund reverses the
   original sale in the **double-entry ledger**, keeping the trial balance at zero
   (the same single refund path used everywhere — ADR-0018).
5. After the call the table refreshes; the row's state becomes **RefundIssued** and
   the Approve/Deny buttons disappear (only `Requested` rows offer actions).

### Deny

Click **Deny** on a `Requested` row → `POST /api/support/admin/rmas/<id>/deny`
(no body, `Idempotency-Key: deny-<id>`), then refresh.

> The refund completion is visible in the **Ledger** screen as a balanced "Refund"
> reversal entry. The E2E test (`e2e-admin/admin.spec.ts`) drives exactly this
> flow and asserts `RefundIssued` plus the ledger reversal.

---

## 5. Ledger — `/ledger`

File: `Components/Pages/Ledger.razor`. Calls
`GET /api/payments/admin/ledger/entries`. Read-only view of the append-only
double-entry ledger (the source of truth for all money facts).

Each row shows the entry **Description**, **Reference**, and its **Lines**,
formatted as `"<AccountCode> D<amount>"` for debits and `"<AccountCode> C<amount>"`
for credits (amounts shown in major units, i.e. `minor / 100`). A confirmed sale
posts a balanced entry; an approved refund posts a balanced reversal. Trial balance
should always net to zero.

---

## 6. Xero sync — `/xero`

File: `Components/Pages/XeroSync.razor`. `InteractiveServer`. Calls
`GET /api/payments/admin/xero/sync-runs` to list prior runs (**Reference**,
**Status**, **Xero journal** id).

Steps:

1. Click **Post yesterday's summary**.
2. The page computes yesterday's date (`UTC - 1 day`, `yyyy-MM-dd`) and `POST`s
   `/api/payments/admin/xero/sync/<date>`, then refreshes the table.
3. A successful run appears with **Status = Posted** and a journal id.

> In dev there is **no live Xero**. The backend uses a `LoggingXeroClient`, so the
> "ManualJournal" is logged rather than posted to a real Xero org (ADR-0017). The
> journal is balanced and nets to zero (asserted by the Xero journal-builder unit
> test).

---

## 7. Imports — `/imports`

File: `Components/Pages/Imports.razor`. `InteractiveServer`. Calls
`GET /api/catalog/admin/import-runs` to list runs (**Importer**, **Read**,
**Accepted**, **Rejected**).

Steps:

1. Click **Run sample importer**.
2. The page `POST`s `/api/catalog/admin/import-runs`, then refreshes the table.
3. A run appears with rows read / accepted / rejected. The default sample importer
   targets **~10,500 rows** (~10,417 accepted / ~83 rejected); this is the seed step
   that populates the catalog so the storefront has products to browse.

> The row count is configurable via **`Importer:TargetRows`** (read by
> `SampleDataImporter`, default `10_500`); CI lowers it (e.g. `400`) to keep the
> projection load light. See [Deployment](./deployment.md).

---

## Admin screen → gateway endpoint reference

| Screen | Read | Action |
|--------|------|--------|
| RMA queue | `GET /api/support/admin/rmas` | `POST /api/support/admin/rmas/<id>/approve` (`{requireReturn:false}`) · `.../deny` |
| Ledger | `GET /api/payments/admin/ledger/entries` | — |
| Xero sync | `GET /api/payments/admin/xero/sync-runs` | `POST /api/payments/admin/xero/sync/<date>` |
| Xero mappings | `GET /api/payments/admin/xero/mappings?tenantId=...` | `POST/PUT/DELETE /api/payments/admin/xero/mappings[/{id}]` |
| Imports | `GET /api/catalog/admin/import-runs` | `POST /api/catalog/admin/import-runs` |
| Offers & pricing | `GET /api/catalog/admin/offers?tenantId=...` | `POST/PUT /api/catalog/admin/offers[/{id}]` |
| Payment accounts | `GET /api/payments/admin/payment-accounts?tenantId=...` | `POST /api/payments/admin/payment-accounts` + lifecycle `submit/activate/suspend/archive` |
| Supplier payouts | `GET /api/payments/admin/supplier-payouts/bank-accounts|instructions?tenantId=...` | bank-account `approve/reject/archive`; instruction create/deactivate |
| Orders | `GET /api/ordering/admin/orders` | cancel unpaid orders; refund confirmed orders via Payments refund path |
| Login | — | `POST /auth/login` → gateway `POST /api/identity/login` + admin-probe |

Money-affecting POSTs send an `Idempotency-Key` header (RMA approve/deny) so
redeliveries are safe.
