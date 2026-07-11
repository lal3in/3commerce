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

> These nav items are live alongside the classic MVP screens below. They are
> tenant-scoped — most take a **Tenant ID** field (the seeded default is
> `00000000-0000-0000-0000-000000000001`). Every action button surfaces the real
> API failure reason (validation `errors`, then `detail`, then `title`, unwrapped by
> `Services/GatewayError.cs`); interactive pages set their success/error banner
> **after** the follow-up reload so the message survives the refresh, and prerender
> is disabled on interactive pages so no button is dead before its circuit connects.

- **Entities & suppliers** (`/entities`, `Components/Pages/Entities.razor`) — list
  and create tenant party records (companies, people, trusts…), run the supplier
  **verification lifecycle** (Draft → submit-verification → verify → activate →
  suspend/archive; buttons carry state-prerequisite tooltips so **Activate** is
  reachable), and work the **supplier change-request approval queue**. Maker-checker
  is enforced: Approve/Reject are **disabled on your own requests** with a hint, the
  deciding operator must differ from the requester, and **Reject requires a reason of
  8–500 characters**. The page loads `/api/identity/me` to know who "you" are, and
  unwraps `ValidationProblem` errors into the banner (e.g. "Only active suppliers can
  be suspended."). Backed by the **Entity** service (`/api/entity/...`).
- **Roles & permissions** (`/rbac`, `Components/Pages/Rbac.razor`) — the dynamic
  RBAC console: view the code-defined permission registry and the tenant's roles
  (seeded catalog: admin/ops/finance/support/merchandiser/customer), editable and
  clonable. Changes are enforced by the Identity/Authz **PDP** and re-evaluate
  active sessions (ADR-0025).
- **Commerce ops** (`/commerce-ops`, `Components/Pages/CommerceOps.razor`) —
  storefront lifecycle, domains, and product-publication operations, plus links into
  the payment/pricing setup pages. The per-storefront **Manage** form closes and
  resets after a successful save (a failed save stays open for retry); a failed
  **Preview** or **Activate** fetches `/readiness` and renders the concrete blocker
  checklist under the error banner (e.g. "at least one domain", "one canonical
  domain") instead of the generic ProblemDetails title.
- **Offers & pricing** (`/offers`, `Components/Pages/Offers.razor`) — Catalog Offer
  CRUD for `(product/variant × supplier)` supply profiles: supply category,
  fulfilment type, price, pricing model, billing period, tiers, priority, and active
  state.
- **Payment accounts** (`/payment-accounts`, `Components/Pages/PaymentAccounts.razor`) —
  Payments-owned tenant/storefront payment accounts: create Draft accounts, submit,
  readiness-gated activate, suspend, and archive. Each row also offers **Edit** (an
  inline form — the name is always editable; provider/mode inputs are disabled with a
  tooltip while the account is Active, so suspend first) and **Make default** (sets
  the tenant's default account and clears every sibling in one tenant-scoped
  transaction; exactly one default, shown by the ★ column; an Archived account cannot
  become default).
- **Supplier payouts** (`/supplier-payouts`, `Components/Pages/SupplierPayouts.razor`) —
  tokenized/masked supplier bank-account setup and active payout instructions. A
  **supplier dropdown** (loaded from `/api/entity/entities`, filtered to Active
  entities; manual GUID entry remains as a fallback) replaces free-typing an id.
  Bank accounts: create → approve/reject/archive plus **Edit** — label-only changes
  keep the account Active; changing banking identity (country / masked routing /
  masked account / vault token) **resets an Active or Rejected account to
  PendingApproval** for re-approval (with a re-approval message). Payout instructions:
  create (the supplier id is taken from the selected bank account so it can't
  mismatch) plus **Edit** (numeric-enum cadence and/or re-point `bankAccountId`; the
  target must be Active). Raw bank details are never entered — only vault token refs
  and masked display values are stored.
- **Xero mappings** (`/xero-mappings`, `Components/Pages/XeroMappings.razor`) —
  ledger-account to Xero-account mapping CRUD with tenant-default, storefront,
  category, supplier, and product override precedence. The **Scope** select carries
  numeric values 1–5 and sends the number on the wire (the platform numeric-enum
  invariant — the earlier string-scope path 400'd on save); **Edit** maps the
  response's scope name back to its numeric value and re-shows the target id.
- **Supplier Portal** (separate app, `:5300`) — suppliers sign in to view
  readiness, upload stock feeds, and raise user/contact/bank **change requests**
  that operators approve here.
- **Security** (`/security`, `Components/Pages/Security.razor`) — three sections:
  **Your second factor (TOTP)** (Enable MFA → add the secret/otpauth URI to an
  authenticator app → confirm with a code → **recovery codes shown once**, store
  them); **Tenant MFA policy** (admin-only `GET/PUT /api/identity/mfa/policy`; the
  effective policy is the max of the platform floor `Mfa:PlatformMinimum` and the
  tenant setting); and **Webhook signing secrets** (per-provider registry —
  add/deactivate; secrets are **always masked** `first4…last4` in responses;
  rotation-safe: active secrets are tried newest-first so rotating never drops
  webhooks).
- **Mission control** (`/mission-control`, `Components/Pages/MissionControl.razor`) —
  the **Activity timeline** (central Audit projection — admin mutations across
  Catalog, Payments, Entity, Identity, Support, and Ordering now emit audit events,
  so it is populated during real operations), **Scheduled jobs** (Workflow service
  run history), a live **Message bus** section (read-only RabbitMQ management API:
  queue/consumer/ready/unacked/dead-letter KPI cards, a red dead-letter table when
  any `*_error`/`*_skipped` queue holds messages, and a busiest-queues top-10;
  unreachable broker degrades to a hint), and **Consoles** links (Grafana RED
  dashboard, RabbitMQ management). Data comes via owning-service APIs only.
- **CLI** (`src/Cli`, `dotnet tool`) — the same operations from a terminal,
  Gateway-only, with explicit `--tenant` scope. `auth login` (with `--code` for
  MFA-enrolled accounts) persists the opaque session to `~/.3commerce/config.json`
  (mode `0600`); `auth whoami`/`auth logout` round out the session. Every command
  group (rbac/entity/supplier/storefront/catalog/payment/payout/xero) issues real
  gateway HTTP; mutations take `--body` raw-JSON passthrough.

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
   - **Authenticator code** — only needed when MFA is enabled on the account
     (enroll on the **Security** page); recovery codes work here too. Leave empty
     otherwise.
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
Commerce ops, Payment accounts, Supplier payouts, Mission control, Security,
plus **Log out**.

---

## 3. Orders — `/orders`

File: `Components/Pages/Orders.razor`. Lists orders from
`GET /api/ordering/admin/orders` with **Order**, **Status**, **Total**, **Placed**,
and actions. Operators can cancel unpaid/pending orders via
`POST /api/ordering/admin/orders/<id>/cancel`; confirmed orders show a **Refund**
action (first click asks "Confirm?", second click fires) that `POST`s
`/api/payments/admin/refunds` with the full order total and a **deterministic
`Idempotency-Key: refund-<orderId>`** — a retried refund of the same order is
idempotent, and the endpoint requires the key. The refund reverses the ledger via
the single refund path; the order's own status stays **Confirmed** (money truth
lives in the ledger). Success/error banners are set after the reload so they
survive the refresh.

---

## 4. RMA queue — `/rmas` (operator approve → refund)

File: `Components/Pages/RmaQueue.razor`. `InteractiveServer` render mode. This is
the key operator money flow.

On load it calls `GatewayClient.GetListAsync(...)` →
`GET /api/support/admin/rmas` and renders a table: **Order**, **Amount**
(`AmountMinor / 100`), **State**, **Reason**, and actions.

### Approve → refund

1. Open `/rmas`. Find the row for the customer's order — state **Requested**.
2. Click **Approve & refund** (refund now, no physical return) or
   **Approve (require return)** (refund only after the item comes back).
   All buttons are disabled while the call runs.
3. The page `POST`s
   `/api/support/admin/rmas/<id>/approve` with body `{ requireReturn: true|false }`
   and an `Idempotency-Key: approve-<id>` header.
4. This triggers the refund saga on the backend:
   **RefundRequested → refund executes → RefundIssued**. The refund reverses the
   original sale in the **double-entry ledger**, keeping the trial balance at zero
   (the same single refund path used everywhere — ADR-0018).
5. After the call the table refreshes; when the saga has applied the transition the
   row's state becomes **RefundIssued** (or **AwaitingReturn** on the require-return
   path, which then offers **Mark return received** → `POST …/return-received` to
   release the refund).

### Saga lag — why a row shows "Updating…"

RMA endpoints return **202 Accepted** and the saga applies the state transition
**asynchronously**, so the read model can briefly lag the action (a second click
used to land a 409 like `"RMA is 'RefundPending', not awaiting a return."`). After
an accepted action the page therefore suppresses that row's buttons — it shows
**Updating… \[Refresh\]** — until a reload observes a *different* state. On failure
the server's own message is surfaced and the queue refreshes so the row shows its
true state.

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
| RMA queue | `GET /api/support/admin/rmas` | `POST /api/support/admin/rmas/<id>/approve` (`{requireReturn:bool}`) · `.../deny` · `.../return-received` |
| Ledger | `GET /api/payments/admin/ledger/entries` | — |
| Xero sync | `GET /api/payments/admin/xero/sync-runs` | `POST /api/payments/admin/xero/sync/<date>` |
| Xero mappings | `GET /api/payments/admin/xero/mappings?tenantId=...` | `POST/PUT/DELETE /api/payments/admin/xero/mappings[/{id}]` (numeric `scope` 1–5) |
| Imports | `GET /api/catalog/admin/import-runs` | `POST /api/catalog/admin/import-runs` |
| Offers & pricing | `GET /api/catalog/admin/offers?tenantId=...` | `POST/PUT /api/catalog/admin/offers[/{id}]` |
| Payment accounts | `GET /api/payments/admin/payment-accounts?tenantId=...` | `POST/PUT /api/payments/admin/payment-accounts[/{id}]` + `make-default` + lifecycle `submit/activate/suspend/archive` |
| Supplier payouts | `GET /api/payments/admin/supplier-payouts/bank-accounts|instructions?tenantId=...` | bank-account create/`PUT`/`approve/reject/archive`; instruction create/`PUT`/deactivate |
| Orders | `GET /api/ordering/admin/orders` | cancel unpaid orders; `POST /api/payments/admin/refunds` (`Idempotency-Key: refund-<orderId>`) |
| Security | `GET /api/identity/mfa/status|policy` · `GET /api/payments/admin/webhook-secrets` | MFA `enroll/begin` + `enroll/confirm` · `PUT /api/identity/mfa/policy` · webhook-secret `POST` + `POST …/{id}/deactivate` |
| Mission control | `GET /api/audit/admin/audit?tenantId=...` · `GET /api/workflow/admin/workflow/runs` · RabbitMQ mgmt API | — (read-only) |
| Login | — | `POST /auth/login` → gateway `POST /api/identity/login` (+ optional MFA code) + admin-probe |

Money-affecting POSTs send an `Idempotency-Key` header (Orders refund, RMA
approve/deny) so redeliveries are safe. Admin mutations across services emit audit
events that feed the Mission Control activity timeline.
