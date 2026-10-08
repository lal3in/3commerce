#!/usr/bin/env bash
#
# e2e-verify.sh — regression verification for 3commerce.
#
# Runs every end-to-end check exercised while building the project, so after you
# add new features you can confirm nothing previously working has broken.
#
# Usage:
#   scripts/e2e-verify.sh            # automated suites only (fast, deterministic, Docker for Testcontainers)
#   scripts/e2e-verify.sh --live     # ALSO boot the full stack and run live user-journey smoke flows
#   scripts/e2e-verify.sh --live-only
#
# Exit code is non-zero if any check fails.
#
# Live mode leaves the machine EXACTLY as it found it. Infra (scripts/lib/infra.sh, all-or-nothing): fully up ->
# used and left up; down -> the FULL set is brought up and taken fully down again at the end (also on Ctrl-C);
# PARTIAL -> refused (heal with scripts/dev-up.sh or clear with scripts/dev-down.sh, then re-run). On CI (CI=true)
# the default set is the core one (Postgres + RabbitMQ + Valkey); INFRA_SET=core|full overrides it, and CI's
# browser-e2e sets `full` on pushes to test/main (ADR-0058) so the portal + observability specs run there. The app stack (gateway + frontends) follows the same rule: reused, or booted + stopped.
#
# ─────────────────────────────────────────────────────────────────────────────
# COVERAGE CHECKLIST  (keep in sync — see the "test list" rule in AGENTS.md)
#
# Automated (encoded in test suites / build):
#   A1  Solution builds with 0 warnings (warnings-as-errors)
#   A2  Formatting clean (dotnet format --verify-no-changes)
#   A3  Backend unit + contract tests (Identity hasher/tokens, customer shopping
#       profile names + typed address defaults, tenant/RBAC/Authz
#       policy engine + PDP resolver, contract equality, DevSecretGuard refuses the
#       committed dev key outside Development — BL-11; Entity domain skeleton invariants;
#       Catalog tenant-scoped ProductModel identifiers/bundles/taxonomy invariants;
#       Catalog Storefront lifecycle plus public URL/currency/tax config invariants;
#       Catalog Publication readiness/SEO/fulfillment-source invariants;
#       Catalog go-live readiness storage: one table per signal + a read-only combined view
#       (StorefrontReadinessModelTests — the race-free shape of the readiness consumers);
#       Catalog Promotion aggregate invariants (ADR-0051): >=1 threshold (money and/or
#       quantity), >=1 reward, percent XOR fixed amount, scope<->product binding,
#       ordered inclusive active window, storefront match, activate/deactivate;
#       Catalog COUPON invariants (ADR-0052): code normalized to trimmed UPPERCASE, unusable
#       characters/over-length rejected, a code-gated promotion may carry NO threshold while an
#       automatic one may not, clearing the code of a thresholdless coupon refused, usage limits
#       null = unlimited and >= 1 when set;
#       Ordering Pricing engine: supplier/selling price inputs, tax-mode seam,
#       fixed/percent/product/category/storefront/bundle/free-shipping promotions,
#       best-discount-wins, quantity-tier promotions, DiscountMinor snapshots,
#       THRESHOLD promotions (money and/or quantity, storefront or product scope,
#       measured on the offer-resolved effective selling price excl. tax/ship/fees) and
#       combinability resolved by the SHARED PromotionEvaluator that checkout also calls —
#       best-exclusive vs sum-of-combinables by customer benefit, tie to the combinable set,
#       ascending-id tiebreak, no-FX currency guard, window bounds, fixed amount clamped to
#       its scope base, and a largest-remainder per-line allocation that sums exactly;
#       Ordering PREVIEW basis (ADR-0054, PromotionPreviewTests): the flat-499 divergence reproduced
#       (free shipping vs a cash discount picking different winners at 499 and at a real rate), a
#       KNOWN rate making the preview identical to the charge, an all-digital cart scoring free
#       shipping at 0, Settled when no shipping amount can change the winner, Provisional reporting
#       the GUARANTEED FLOOR with free shipping left undecided (and still allocating exactly), the
#       coupon variant, and a 400-trial sweep proving the preview never contradicts the charge;
#       Ordering COUPON gate + refusal reasons (ADR-0052): a code-gated promotion applies only on a
#       trimmed case-insensitive code match while an automatic one is untouched by any entered code,
#       stacking is still just the Combinable flag, a coupon out-competed by a better promotion loses,
#       and one fact per CouponStatus (unknown/inactive/not started/expired/wrong storefront-or-currency/
#       usage limit before threshold/per-customer limit/applied) plus the customer-key rule that makes a
#       guest checkout count (CouponTests);
#       Ordering per-line REFUND BASIS allocation (ADR-0055, OrderLineDiscountsTests): an order's whole
#       discount split across its lines — a uniform storefront percentage spread by line value, a
#       product-scoped promotion staying on the line it covers, the two stacked, rounding remainders
#       distributed so the parts sum exactly, and the subtotal clamp never allocating more than the
#       order actually discounted;
#       Ordering CHECKOUT GATES (ADR-0059, CheckoutGateTests + OfferResolutionTests): a cart in another
#       currency than its storefront's is refused (message names both currencies; no projected copy = not
#       gated), and supply availability is Catalog's ADR-0048 rule per storefront AND currency — offerless
#       = available, an approved offer only for another store/currency neither unlocks nor blocks, window
#       ignored for coverage;
#       ITaxStrategy home-regime/default-zero/export zero-rating behavior;
#       Ordering CheckoutAttempt before Order, per-storefront
#       order-number sequence, and campaign/storefront checkout snapshot seam;
#       Payments PaymentAccount lifecycle/readiness, tenant default/storefront override,
#       saved card/customer vault snapshots and active-method rules,
#       active-only checkout snapshot, provider mode snapshot, supplier bank approval,
#       payout instruction routing, supplier payable policy, balanced payable accrual,
#       and Xero tenant/storefront/category/supplier/product mapping precedence;
#       admin payment-account lifecycle endpoints (Draft→submit→activate, Live readiness guard — PaymentAccountAdminTests);
#       admin supplier-payout setup endpoints (masked bank account approval + payout instruction — SupplierPayoutAdminTests);
#       admin Xero mapping CRUD endpoints (XeroMappingAdminTests);
#       gateway production YARP config conventions + internal health-route block (GatewayConfigTests);
#       Kafka stream envelope/topic/fake-producer/consumer/outbox-relay/domain-fact/privacy/replay/resilience contract guards (ContractTests, ADR-0034);
#       Quartz persistent scheduler config guards (ContractTests, msg_11);
#       Ordering variant-aware cart/projection: ProductCopies carry variants,
#       cart lines key by product+variant, and checkout/order lines snapshot variants)
#   A3d Unit · one receive endpoint per service (ADR-0060, ConsumerEndpointNameTests): every consumer/saga
#       queue name the shared kebab formatter derives — across all services.sh services + Notifications —
#       belongs to ONE service (a shared name = competing consumers); BuildingBlocks defines no consumers;
#       no explicit endpoint names / AddMassTransit outside AddServiceBus
#   A4  Integration · spine: outbox atomicity, durable redelivery, inbox idempotency
#       (every fixture's teardown also fails the run if a test left a service host running —
#       TestHostTracker; the check below treats an xUnit 'Cleanup Failure' as a failure)
#   A5  Integration · Identity auth: register no-enumeration, logout revocation,
#       /me requires claims, wrong password rejected, reset revokes sessions;
#       master-admin user mgmt (list / reset temp password / change email) (AdminUserManagementTests);
#       role deletion refuses built-in + in-use roles (RoleDeletionTests)
#   A5b Integration · Tenant RLS: transaction-scoped SET LOCAL isolates rows, fails closed,
#       no cross-scope leak, MasterGlobal bypass; Users + Entities FORCE-RLS proven as a
#       non-superuser owner (tenant isolation / platform scope / fail-closed reads AND writes),
#       via the per-request TenantScopeMiddleware (EntityRlsTests, IdentityUsersRlsTests) (ADR-0024)
#   A6  Integration · Catalog: import ≥10k SKUs, exact search, typo fallback,
#       filters, search + product-detail p95 < 500ms (NFR-5), hostile-input safety;
#       admin catalog editor CRUD — create/edit variants+stock+images+attrs, slug
#       uniqueness, category-required, admin-only (FR-12/BL-2)
#       Catalog go-live readiness projection under concurrency: carrier + payment events for 20
#       new storefronts at once → every row has both flags, no 23505/40001, nothing in _error;
#       redelivery/re-publish idempotent, one signal never clobbers the other; a burst of one
#       signal for one storefront applies in order with no 40001 (serial endpoints)
#       (StorefrontReadinessConcurrencyTests)
#   A6b Integration · Ledger invariant: balanced entry commits, unbalanced rejected,
#       append-only (UPDATE/DELETE blocked)
#   A6c Integration · Money flow: guest checkout saga → confirmed + balanced sale,
#       duplicate webhook = one entry, refund reverses + ledger stays balanced;
#       saga survives an Ordering-host outage mid-payment (NFR-2 chaos/BL-6);
#       admin order cancel guard (confirmed→409 refund-instead, unknown→404);
#       threshold promotions end-to-end (ADR-0051): free shipping above the money threshold,
#       the below-threshold control, product-scoped discount touching only that product's
#       lines, the threshold measured on the OFFER-resolved price (not the catalog price),
#       combinables stacking past a bigger exclusive, and a promotion stacking with the
#       storefront-wide discount with tax on the doubly-discounted base — trial balance 0 in
#       every case (no new ledger line);
#       the promotion x discount x shipping MATRIX with the preview and the charge asserted on the
#       SAME cart (ADR-0054, PromotionMatrixTests): the free-shipping-vs-cash-discount race at a real
#       rate (plus the winner the old 499 guess picked), the provisional path for a shippable cart with
#       no address, an all-digital cart scoring free shipping at 0, two exclusives never summing, an
#       exclusive beating a smaller stack, a tie going to the combinable set, an unmet threshold,
#       free shipping + store-wide discount, free shipping + a product-scoped threshold, coupon +
#       free shipping + store-wide together, a coupon losing the contest without burning its
#       allowance, and a stack capped at the subtotal — each asserting
#       Net - Discount + Ship + Tax = Gross and trial balance 0;
#       Catalog PromotionChanged → Ordering PromotionCopy projection: insert, idempotent
#       re-consume (no duplicate row), deactivation (PromotionProjectionTests);
#       STOREFRONT-SCOPED TAX (ADR-0055, StorefrontTaxScopingTests): two live storefronts sharing ONE
#       currency at 0% exclusive and 25% INCLUSIVE each charge their own rate and their own regime
#       (the old by-currency lookup gave the 0% store the other's 2500 bps and inclusiveness), a
#       low-rate store is not bled into by a louder same-currency neighbour, another TENANT's live
#       store in the same currency is not a tax source, and a storefront that is not live is refused
#       at checkout rather than sold untaxed — money identity + trial balance 0 on every settled path;
#       CHECKOUT CURRENCY + OFFER GATE (ADR-0059): a EUR cart on an AUD storefront is a 400 naming both
#       currencies, flagged first by /cart/summary (checkoutBlock=CurrencyMismatch, unpriced), nothing
#       booked, and the same store sells once the cart is in AUD (CartCurrencyTests); a subscription
#       bought under the gate still renews in its stored currency and books a balanced renewal entry
#       (SubscriptionRenewalPriceTests); and Catalog's listing/detail and Ordering's checkout AGREE per
#       storefront + currency on offers created through Catalog's real admin API — approved only for
#       another store or only in another currency → hidden AND 400; an unapproved offer elsewhere no
#       longer blocks a listed product → listed AND 201 at catalogue price; approved here → listed AND
#       201 at the offer price; offerless → listed AND 201 (CheckoutOfferGateParityTests);
#       PROMOTION SCOPE + CAPS (PromotionScopeAndCapTests): the per-customer limit under EIGHT
#       concurrent checkouts (the only read-then-write window, held by an advisory lock — proven to
#       fail without it), a storefront-scoped promotion discounting its own store and no other while
#       an all-storefront one reaches every store of its currency, a store-wide discount plus a
#       promotion jointly capped at the subtotal (goods free, never negative, shipping still charged,
#       trial balance 0), and the promotion NAME snapshotted on the order beside its id;
#       PROMOTION ADMIN GUARDS (PromotionAdminGuardTests): a promotion aimed at a storefront of
#       another currency is refused at write time instead of silently never applying, and a coupon can
#       be turned into an automatic promotion in ONE request (drop the code, supply the threshold)
#       while dropping the code with no threshold is still refused;
#       SUBSCRIPTION RENEWAL PRICE (ADR-0057, SubscriptionRenewalPriceTests + CouponTests): a promotion
#       flagged AppliesToRenewals keeps its discount for the life of the subscription while an
#       introductory one discounts the first period only and renewals go back to list, only the flagged
#       half of a STACK rides, the renewal discount never exceeds the discount actually given, and the
#       storefront-wide discount never rides a renewal (first period 1300, renewal 1500); the cart
#       PREVIEW reports the ongoing price too — 1300 today / 2000 from next month on an introductory
#       deal, a permanent promotion's own 1500, and no renewal row at all for a one-time cart;
#       REFUND vs REDEMPTION (ADR-0056, CouponRefundPolicyTests): a confirmed redemption is spent for
#       good — a full refund, a chargeback and a partial refund each leave it Confirmed with the
#       counter untouched and the shopper refused the code a second time, while the release contrast
#       (a checkout that never confirms) stays in CouponRedemptionTests;
#       COUPON ALLOWANCE + PERSISTED ALLOCATION (CouponAllowanceTests): a per-customer hold stranded by
#       a crash is reclaimed instead of locking that shopper out of the coupon forever (while a hold
#       taken minutes ago still refuses the second try), a free-shipping code on an all-digital cart
#       saves nothing so it burns no allowance and the next shopper still gets it, and the per-line
#       discount checkout PERSISTS is read back from the database — a product-scoped promotion lands
#       wholly on its own line and the vector sums to PromotionDiscountMinor exactly;
#       coupon codes end-to-end (ADR-0052, CouponRedemptionTests): the code is REQUIRED for the
#       discount and is actually charged; the cap holds under TEN CONCURRENT checkouts against
#       MaxRedemptions=3 (exactly 3 win, the counter matches the redemption rows); the per-customer
#       limit counts a guest by checkout email across a fresh browser/casing; a failed payment
#       releases the hold so a single-use code is spendable again (and is genuinely blocked while
#       held); redelivered CheckoutCompleted/OrderCancelled neither double-confirm nor double-release;
#       every refusal reports its own reason on /cart/summary and at checkout; and a reservation whose
#       checkout attempt never committed is swept so the cap recovers — trial balance 0;
#       one distributed trace spans the HTTP + MassTransit hops (NFR-7/BL-7)
#   A6d Integration · RMA saga: approve → refund → RefundIssued, double-approve no-op,
#       deny path + require-return → AwaitingReturn → return-received releases the refund;
#       per-line RMA derives the refund server-side from the order snapshot
#       (BL-8);
#       Support order snapshot under concurrency: 3 OrderConfirmed copies (distinct message ids) for
#       each of 20 orders at once → one snapshot per order with its lines, no 23505/40001, nothing in
#       order-snapshot_error (endpoint partitioned by order id); a redelivery / re-publish is a no-op
#       (SupportOrderSnapshotConcurrencyTests);
#       DISCOUNTED REFUND BASIS (ADR-0055, RmaRefundBasisTests): a full return of a discounted order
#       is capped at the refundable gross and reaches RefundIssued instead of stranding in
#       RefundPending forever, a partial return refunds the line's DISCOUNTED value (2800, not the
#       4000 it was listed at) and the customer is shown that same number, a line's discount is
#       pro-rated across a partially returned quantity, a pre-ADR-0055 snapshot still refunds but
#       never above the captured gross, a second request can only claim what is left of the gross
#       (nothing left ⇒ 400), and a refund Payments cannot cover ends the RMA in a terminal
#       RefundFailed state instead of going quiet — trial balance 0 throughout;
#       Fulfillment: shipments grouped by source, idempotent;
#       Fulfillment order intake under concurrency: 3 OrderConfirmed copies (distinct message ids) for each
#       of 20 orders at once → exactly one shipment per order+source, and for 10 out-of-stock orders exactly
#       one HeldOrder + one active inventory hold; no 23505/40001, nothing in fulfillment-order-confirmed_error
#       (endpoint partitioned by order id); a redelivery / re-publish is a no-op
#       (FulfillmentShipmentConcurrencyTests)
#   A6e Unit · Xero journal builder: groups by account, nets to zero, skips empty days
#   A6f Integration · Phase 4 shipping/inventory/fulfilment: reservations + inventory-movement
#       ledger, confirm-on-order stock consumption, carrier quotes (Fake/AusPost/DHL/FedEx/UPS/
#       StarTrack/Pack&Send) + default-parcel fallback + selected checkout shipping amount,
#       revalidation, dropship auto-forward, packages/labels/tracking, manual restock,
#       order holds (auto inventory hold → release → fulfil)
#   A6g Integration · Phase 7 digital supply & billing: a digital line issues an entitlement (no
#       shipment), the non-physical product matrix (download/subscription/usage/manual-service)
#       maps to expected entitlements without shipments, and a mixed order ships physical + entitles
#       digital; a recurring line sets up a
#       subscription that renews (charge via the rail) + cancels; usage metering rolls records into
#       balances incrementally + idempotently, gates access when overage is off, and bills overage once
#   A6h Unit · Phase 6 compliance/ops primitives (ADR-0029) — run per filter:
#       Audit (hash-chain append/verify/tamper + stream-outbox audit fact staging) · SensitiveAudit (coverage taxonomy + denied attempt) ·
#       ApprovalWorkflow (maker-checker/service-acct/MasterGlobal/expiry) · WebhookDelivery (HMAC sign,
#       anti-SSRF, retry backoff, dispatcher) · ProviderWebhook (inbound verify + replay window) ·
#       Export (CSV RFC4180, signed expiring download, GDPR redaction) · Storage (object-store round-trip,
#       traversal, upload allow-list, image variants) · MfaPolicy (platform-min/tenant-strengthen/step-up) ·
#       Notifications (security-always/marketing-opt-in + minimal alert content) · Region (no region move,
#       retention Retain/Redact/Purge). Plus Payments JobExecutor (scheduled-run success/failure).
#   A6c Browser E2E (Playwright) · preview parity (ADR-0054, e2e/promotion-shipping-parity.spec.ts):
#       a shippable cart with no address shows "free shipping may apply" and never the decided row,
#       and entering an address at checkout quotes the real carrier rate, settles the contest on it,
#       and the cart then renders the decided reward with no hedging
#   A7  Storefront typecheck (tsc) + production build (next build), including
#       auth-aware checkout prefill/review, checkout +/- recalculation, and
#       authenticated confirmation hiding guest account conversion
#   A8  No vulnerable NuGet packages
#
# Live full-stack (only with --live; exercises the gateway + storefront paths the
# in-process integration tests do not):
#   L1  Infra healthy: the WHOLE infra set up (all-or-nothing, scripts/lib/infra.sh — every container running +
#       healthy/probed; the core set on CI) + Postgres holds all service DBs from init-databases.sql
#   L2  All six services report /health/ready
#   L3  Ping-pong spine flows through the gateway to the Notifications worker
#   L4  Gateway blocks internal health routes (/api/*/health* → 404)
#   L5  Register → 202, identical body on repeat (no user enumeration)
#   L6  Verification email token delivered; verify-email succeeds
#   L7  Login sets cookie; /me with cookie → 200, without → 401
#   L8  Saved address create → 201
#   L9  Admin RBAC: customer → 403, admin authorized
#   L10 Catalog import → ≥10k accepted, >0 rejected
#   L11 Search: exact (X-Total-Count), typo fallback, category+attribute filter, detail
#   L12 Search latency p95 < 500ms
#   L13 Logout → 204; password reset → login with new password
#   L14 Storefront SSR: home/search/product render catalog data; /account redirects
#   L15 Cart: add a product SELLABLE on the demo store (its own listing, in its currency) → cart accepts it
#   L16 Checkout on that store, shipped to a country it serves: order + clientSecret + gross (returns at
#       intent); a refusal prints the HTTP status + problem+json body
#   L17 Simulate payment → saga confirms the order
#   L17b The confirmed order reached EVERY OrderConfirmed subscriber: a Fulfillment shipment (or hold) AND the
#       Notifications confirmation email (ADR-0060 — a queue shared by two services splits them)
#   L18 Ledger: balanced sale posted, trial balance zero
#   L19 Admin refund → ledger reversal, trial balance stays zero
#   L20 Storefront + Admin E2E in a real browser (Playwright): storefront browsing,
#       fixture-manifest catalog scenario products (when seeded), cart + full guest checkout
#       (test payment), account flows; admin login, broad operations page rendering
#       (catalog/offers/promotions/orders/commerce ops/payments/payouts/Xero/mission control),
#       RMA action availability, supplier portal readiness/stock/change-request flows,
#       operator RMA approve → refund → RefundIssued + ledger reversal, threshold-promotion
#       authoring through the admin modal (round-tripped via the API, then deactivated),
#       the shopper seeing the promotion row + free shipping in the cart and checkout summary,
#       COUPON authoring through the same modal (code round-tripped in canonical UPPERCASE, the
#       Redemptions column, no threshold required, duplicate code refused) and the shopper applying
#       a code at checkout — invisible until entered, an unknown code showing its OWN reason, the
#       discount row appearing, and remove pricing the cart back at full price (ADR-0052);
#       a cart filled on the EU store and opened on the AU store shows the checkout-blocked notice
#       naming EUR and AUD before checkout (ADR-0059, e2e/currency-tax.spec.ts; needs --data full);
#       broken-image guards: zero broken images on the storefront (e2e/broken-images.spec.ts) and the
#       admin Catalog (e2e-admin/broken-images.spec.ts), where a thumbnail or image-URL preview whose
#       host is unreachable (request aborted) degrades to the bundled /img/image-placeholder.svg
# ─────────────────────────────────────────────────────────────────────────────

set -uo pipefail
cd "$(dirname "$0")/.."
ROOT="$(pwd)"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"

GATEWAY="http://localhost:8080"
STOREFRONT="http://localhost:3000"
MODE="auto"
[[ "${1:-}" == "--live" ]] && MODE="auto+live"
[[ "${1:-}" == "--live-only" ]] && MODE="live"

[[ "${CI:-}" == true ]] && export INFRA_SET="${INFRA_SET:-core}"
source "$ROOT/scripts/lib/infra.sh"
source "$ROOT/scripts/lib/procs.sh"

PASS=0 FAIL=0
declare -a FAILED=()

pass() { printf '  \033[32m✓\033[0m %s\n' "$1"; PASS=$((PASS+1)); }
fail() { printf '  \033[31m✗ %s\033[0m\n' "$1"; FAIL=$((FAIL+1)); FAILED+=("$1"); }
stage() { printf '\n\033[1m== %s ==\033[0m\n' "$1"; }
skip() { printf '  \033[33m- %s (skipped)\033[0m\n' "$1"; }
# check "<label>" <expected_substring> <command...>  — passes if command output contains substring
check() {
  local label="$1" want="$2"; shift 2
  local out; out="$("$@" 2>&1)"
  if [[ "$out" == *"$want"* ]]; then pass "$label"; else fail "$label (wanted '$want')"; fi
}

# ── Automated suites ─────────────────────────────────────────────────────────
run_automated() {
  stage "A1–A2  Build + format"
  if dotnet build "$ROOT/3commerce.sln" 2>&1 | grep -q '0 Warning(s)'; then pass "A1 build, 0 warnings"; else fail "A1 build/warnings"; fi
  if dotnet format "$ROOT/3commerce.sln" --verify-no-changes >/dev/null 2>&1; then pass "A2 format clean"; else fail "A2 format"; fi

  stage "A3  Backend unit + contract tests"
  if dotnet test "$ROOT/3commerce.sln" --no-build --filter 'Category!=Integration' 2>&1 | grep -q 'Failed: *0'; then pass "A3 unit/contract"; else fail "A3 unit/contract"; fi

  stage "A3b Promotions + coupons (ADR-0051/0052/0054) — Catalog domain, shared evaluator, preview basis, engine, coupon gate"
  if dotnet test "$ROOT/3commerce.sln" --no-build \
      --filter 'Category!=Integration&(FullyQualifiedName~PromotionTests|FullyQualifiedName~PromotionEvaluatorTests|FullyQualifiedName~PromotionPreviewTests|FullyQualifiedName~PricingTests|FullyQualifiedName~CouponTests)' 2>&1 \
      | grep -q 'Failed: *0'; then pass "A3b promotions + coupons"; else fail "A3b promotions + coupons"; fi

  stage "A3c Checkout gates (ADR-0059) — storefront currency + per-storefront/currency supply availability"
  if dotnet test "$ROOT/3commerce.sln" --no-build \
      --filter 'Category!=Integration&(FullyQualifiedName~CheckoutGateTests|FullyQualifiedName~OfferResolutionTests)' 2>&1 \
      | grep -q 'Failed: *0'; then pass "A3c checkout gates"; else fail "A3c checkout gates"; fi

  stage "A3d One receive endpoint per service (ADR-0060) — no queue shared across services"
  if dotnet test "$ROOT/tests/3commerce.IntegrationTests" --no-build \
      --filter 'Category!=Integration&FullyQualifiedName~ConsumerEndpointNameTests' 2>&1 \
      | grep -q 'Failed: *0'; then pass "A3d unique receive endpoints"; else fail "A3d unique receive endpoints"; fi

  stage "A4–A6  Integration tests (Testcontainers — Docker required)"
  local out; out="$(dotnet test "$ROOT/tests/3commerce.IntegrationTests" --no-build --filter 'Category=Integration' 2>&1)"
  # A fixture teardown failure (e.g. TestHostTracker: a test left a service host running) still prints
  # "Failed: 0" but exits non-zero, so check for it explicitly.
  if grep -q 'Failed: *0' <<<"$out" && ! grep -q 'Cleanup Failure' <<<"$out"; then
    local n; n="$(grep -oE 'Passed: *[0-9]+' <<<"$out" | grep -oE '[0-9]+' | tail -1)"
    pass "A4–A6 integration ($n passed)"
  else
    fail "A4–A6 integration"; grep -E 'Failed!|\[FAIL\]|Cleanup Failure' <<<"$out" | head -5
  fi

  stage "A7  Storefront typecheck + build"
  if [[ -d "$ROOT/src/Storefront/node_modules" ]]; then
    ( cd "$ROOT/src/Storefront" && npx tsc --noEmit >/dev/null 2>&1 ) && pass "A7a tsc clean" || fail "A7a tsc"
    # Own build dir: `next build` rewrites its output wholesale, and sharing .next with a running
    # `next dev` pulls chunks out from under it (every page 500s with MODULE_NOT_FOUND).
    ( cd "$ROOT/src/Storefront" && NEXT_DIST_DIR=.next-verify npm run build >/dev/null 2>&1 ) && pass "A7b next build" || fail "A7b next build"
  else
    fail "A7 storefront deps missing (run: cd src/Storefront && npm install)"
  fi

  stage "A8  Vulnerable package scan"
  if dotnet list "$ROOT/3commerce.sln" package --vulnerable --include-transitive 2>&1 | grep -q 'has the following vulnerable'; then
    fail "A8 vulnerable packages found"
  else
    pass "A8 no vulnerable packages"
  fi
}

# ── Live full-stack smoke ────────────────────────────────────────────────────
wait_health() { # port — up to ~120s (cold .NET start of 8 processes on a slow CI runner)
  for _ in $(seq 1 60); do curl -fsS "localhost:$1/health/ready" >/dev/null 2>&1 && return 0; sleep 2; done
  return 1
}

wait_http() { # url (waits up to ~120s — covers the storefront production build)
  for _ in $(seq 1 60); do curl -fsS -o /dev/null "$1" 2>/dev/null && return 0; sleep 2; done
  return 1
}

# Is a dev stack already up? full → run the live checks AGAINST it (never build over it, never tear it
# down); none → boot our own and tear down only what we started; partial → refuse, because booting a second
# copy over half a stack is how ports get stolen and a running dev storefront's .next gets rebuilt from under it.
stack_state() {
  local up=0 total=0 p
  curl -fsS -o /dev/null -m 3 "$GATEWAY/health" 2>/dev/null && up=$((up+1)); total=$((total+1))
  for p in 3000 5200 5300; do
    total=$((total+1))
    curl -s -o /dev/null -m 5 "http://localhost:$p/" 2>/dev/null && [[ -n "$(lsof -nP -iTCP:$p -sTCP:LISTEN -t 2>/dev/null)" ]] && up=$((up+1))
  done
  if (( up == total )); then echo full; elif (( up == 0 )); then echo none; else echo partial; fi
}

# Stops exactly what this run started — the app processes, then (if this run brought it up) the whole infra set —
# and nothing else. Runs once: at the end of run_live, or from the INT/TERM trap.
LIVE_TORN=0 APP_BOOTED=0
live_teardown() {
  (( LIVE_TORN )) && return 0; LIVE_TORN=1
  if (( APP_BOOTED )); then
    stage "Tearing down"
    "$ROOT/scripts/run-all.sh" stop >/dev/null 2>&1
    # By port, gracefully (TERM then KILL) — never `pkill -f <pattern>`, which also hit processes this run
    # did not start (a developer's own storefront/admin), and is the hazard dev-down.sh documents.
    reap_port 3000 storefront; reap_port 5200 admin; reap_port 5300 supplier-portal
    echo "  services + frontends this run started are stopped"
  elif (( STACK_OWNED )); then
    stage "Tearing down"
    echo "  app stack: never booted, so nothing to stop."
  else
    stage "Leaving the reused stack running"
    echo "  app stack: nothing started, so nothing stopped."
  fi
  if (( INFRA_OWNED )); then
    if infra_down >"$ROOT/.run/e2e-infra.log" 2>&1; then
      echo "  infra: this run brought it up, so it is now fully down again (data volume kept)"
    else
      fail "teardown: infra NOT fully down (see .run/e2e-infra.log)"; tail -15 "$ROOT/.run/e2e-infra.log"
    fi
  else
    echo "  infra: was already fully up — left running, exactly as found"
  fi
}

run_live() {
  STACK_OWNED=1; INFRA_OWNED=0
  mkdir -p "$ROOT/.run"
  infra_load || { fail "live: cannot read the infra set (docker compose config failed)"; return; }
  local infra irc
  infra="$(infra_state)"; irc=$?
  case $irc in
    0) ;;
    1) INFRA_OWNED=1 ;;
    2)
      stage "Refusing: the infra is PARTIAL"
      echo "  $infra"
      echo "  The infra is all-or-nothing (scripts/lib/infra.sh). Heal it to full with scripts/dev-up.sh, or take it"
      echo "  fully down with scripts/dev-down.sh, then re-run — never run the suite on half an infra."
      fail "live: partial infra (refused rather than run on it)"
      return
      ;;
    *)
      stage "Refusing: infra state unknown"
      echo "  $infra"
      fail "live: infra state unknown ($infra)"
      return
      ;;
  esac
  case "$(stack_state)" in
    full)
      if (( INFRA_OWNED )); then
        stage "Refusing: the app stack is up but the infra is DOWN"
        echo "  gateway + frontends answer while no infra container runs — bring the stack fully down"
        echo "  (scripts/dev-down.sh) or fully up (scripts/dev-up.sh --with-frontends), then re-run."
        fail "live: app stack up on a down infra (refused)"
        return
      fi
      STACK_OWNED=0
      stage "Reusing the running stack"
      echo "  gateway + storefront + admin + supplier portal are already up — running the live checks AGAINST"
      echo "  them: no build, no migrations, no restarts, and no teardown (the stack stays exactly as it was)."
      ;;
    partial)
      stage "Refusing: a PARTIAL stack is running"
      echo "  Some of gateway :8080 / storefront :3000 / admin :5200 / supplier :5300 are up and some are not."
      echo "  Either bring it fully up (scripts/dev-up.sh --with-frontends) or down (scripts/dev-down.sh), then re-run."
      fail "live: partial stack (refused rather than boot over it)"
      return
      ;;
  esac
  trap 'live_teardown; exit 130' INT TERM

  stage "L1  Infra (all-or-nothing — the $INFRA_SET set, scripts/lib/infra.sh)"
  if (( INFRA_OWNED )); then
    echo "  infra is DOWN — bringing the whole set up (and taking it fully down again at the end)"
    if infra_up >"$ROOT/.run/e2e-infra.log" 2>&1; then
      pass "L1a infra fully up ($(tail -1 "$ROOT/.run/e2e-infra.log" | sed 's/^ *infra: //'))"
    else
      fail "L1a infra NOT fully up (see .run/e2e-infra.log)"; tail -20 "$ROOT/.run/e2e-infra.log"
      live_teardown; trap - INT TERM
      return
    fi
  else
    pass "L1a infra already fully up — using it, leaving it up"
  fi

  if (( STACK_OWNED )); then
  # Wait up to ~180s for the init script to create all service databases (slow/loaded CI
  # runners create them sequentially; use the loop's own count so a late-landing
  # database isn't missed by a single-shot re-count).
  expected="$(grep -c '^CREATE DATABASE' "$ROOT/infra/postgres/init-databases.sql")"
  dbcount=0
  for _ in $(seq 1 90); do
    dbcount="$(docker exec 3commerce-postgres psql -U postgres -tc '\l' 2>/dev/null | grep -c '_db')"
    [[ "$dbcount" == "$expected" ]] && break; sleep 2
  done
  [[ "$dbcount" == "$expected" ]] && pass "L1 $expected service databases" || fail "L1 service databases (saw '$dbcount', expected '$expected')"

  stage "Applying migrations"
  # ALL db-owning services (mirrors dev-up.sh / scripts/lib/services.sh) — not just the seven core ones.
  # Audit, Workflow, Marketing, Usage, Pricing and Entitlement do NOT self-migrate at startup, so leaving
  # them out left their DBs table-less: /api/audit/admin/audit and /api/workflow/admin/workflow/runs 500,
  # which crashes the (unguarded) Mission Control load → every commerce/revenue tile reads 0; and the
  # marketing/usage job endpoints 500, so the scheduled-jobs monitor lists no jobs. The L20 admin specs
  # (per-currency revenue, variable-decimals, scheduled jobs + run-now) assert that data, so migrate the lot.
  for svc in Identity Catalog Entity Ordering Payments Fulfillment Support Marketing Pricing Audit Workflow Entitlement Usage; do
    dotnet ef database update -p "$ROOT/src/Services/$svc/Infrastructure" -s "$ROOT/src/Services/$svc/Api" >/dev/null 2>&1 \
      && printf '  migrated %s\n' "$svc" || printf '  (migrate %s skipped/failed)\n' "$svc"
  done

  stage "Booting services"
  APP_BOOTED=1
  mkdir -p "$ROOT/.run"
  dotnet build "$ROOT/3commerce.sln" >/dev/null 2>&1
  : > "$ROOT/.run/notifications.log" 2>/dev/null || true
  "$ROOT/scripts/run-all.sh" start >/dev/null
  # Wait for service health BEFORE the CPU-heavy storefront build (avoids startup contention).
  local ok=1; for p in 5101 5102 5103 5104 5105 5106 5107; do wait_health "$p" || ok=0; done
  [[ $ok == 1 ]] && pass "L2 seven services /health/ready" || { fail "L2 service health"; for s in "$ROOT"/.run/*.log; do echo "--- $s"; tail -15 "$s"; done; }

  stage "Booting storefront + admin + supplier portal"
  ( cd "$ROOT/src/Storefront" && NEXT_DIST_DIR=.next-verify npm run build >/tmp/3c-sf-build.log 2>&1 && NEXT_DIST_DIR=.next-verify GATEWAY_URL="$GATEWAY" npm run start:standalone >"$ROOT/.run/storefront.log" 2>&1 & )
  # Run the managed DLLs directly (no apphost — the solution build doesn't always emit one in CI).
  local admin_dll="$ROOT/src/Admin/bin/Debug/net10.0/3commerce.Admin.dll"
  if [[ -f "$admin_dll" ]]; then
    ( ASPNETCORE_URLS="http://localhost:5200" ASPNETCORE_ENVIRONMENT=Development dotnet "$admin_dll" >"$ROOT/.run/admin.log" 2>&1 & )
  else
    echo "  WARNING: admin DLL not found at $admin_dll — admin E2E will be skipped"
  fi
  local supplier_dll="$ROOT/src/SupplierPortal/bin/Debug/net10.0/3commerce.SupplierPortal.dll"
  if [[ -f "$supplier_dll" ]]; then
    ( ASPNETCORE_URLS="http://localhost:5300" ASPNETCORE_ENVIRONMENT=Development dotnet "$supplier_dll" >"$ROOT/.run/supplier-portal.log" 2>&1 & )
  else
    echo "  WARNING: supplier portal DLL not found at $supplier_dll — supplier E2E will be skipped"
  fi

  else
    # Reusing: still PROVE the running services are healthy rather than assuming it.
    local ok=1; for p in 5101 5102 5103 5104 5105 5106 5107; do wait_health "$p" || ok=0; done
    [[ $ok == 1 ]] && pass "L2 seven services /health/ready" || fail "L2 service health"
  fi

  stage "L3–L4  Gateway routing"
  # Only a PONG written AFTER this ping counts. On a reused stack the log already holds earlier PONGs (a stale
  # match would pass instantly), and bare-run opens logs with '>' so truncating one under a live writer just
  # leaves a NUL-filled gap — so remember the size now and search only what is appended after it.
  local pong_from; pong_from=$(( $(wc -c < "$ROOT/.run/notifications.log" 2>/dev/null || echo 0) + 1 ))
  check "L3 ping-pong via gateway → worker" "PONG received" bash -c \
    "curl -fsS -X POST $GATEWAY/api/catalog/ping >/dev/null; for _ in \$(seq 1 60); do if tail -c +$pong_from '$ROOT/.run/notifications.log' 2>/dev/null | grep -aq 'PONG received'; then tail -c +$pong_from '$ROOT/.run/notifications.log' | grep -a 'PONG received' | tail -1; exit 0; fi; sleep 1; done; exit 1"
  check "L4 gateway blocks internal health" "404" bash -c \
    "curl -s -o /dev/null -w '%{http_code}' $GATEWAY/api/ordering/health/ready"

  stage "L5–L8  Auth lifecycle"
  local jar=/tmp/3c-e2e-cookies.txt; rm -f "$jar"
  local email="e2e-$(date +%s)@example.com"
  local b1 b2
  b1="$(curl -s -X POST $GATEWAY/api/identity/register -H 'content-type: application/json' -d "{\"email\":\"$email\",\"password\":\"a-strong-password\"}")"
  b2="$(curl -s -X POST $GATEWAY/api/identity/register -H 'content-type: application/json' -d "{\"email\":\"$email\",\"password\":\"a-strong-password\"}")"
  [[ "$b1" == "$b2" && -n "$b1" ]] && pass "L5 register no-enumeration" || fail "L5 register"
  sleep 3
  local token; token="$(grep -aoE 'verify-email\?token=[A-Za-z0-9_-]+' "$ROOT/.run/notifications.log" | tail -1 | cut -d= -f2)"
  check "L6 verify-email" "verified" bash -c \
    "curl -s -X POST $GATEWAY/api/identity/verify-email -H 'content-type: application/json' -d '{\"token\":\"$token\"}'"
  curl -s -c "$jar" -X POST $GATEWAY/api/identity/login -H 'content-type: application/json' -d "{\"email\":\"$email\",\"password\":\"a-strong-password\"}" >/dev/null
  check "L7a /me with cookie → 200" "200" bash -c "curl -s -o /dev/null -w '%{http_code}' -b '$jar' $GATEWAY/api/identity/me"
  check "L7b /me without cookie → 401" "401" bash -c "curl -s -o /dev/null -w '%{http_code}' $GATEWAY/api/identity/me"
  check "L8 add address → 201" "201" bash -c \
    "curl -s -o /dev/null -w '%{http_code}' -b '$jar' -X POST $GATEWAY/api/identity/me/addresses -H 'content-type: application/json' -d '{\"name\":\"E2E\",\"line1\":\"1 St\",\"city\":\"Berlin\",\"postcode\":\"10115\",\"country\":\"DE\"}'"

  stage "L9–L12  Catalog: RBAC, import, search"
  local admin=/tmp/3c-e2e-admin.txt; rm -f "$admin"
  curl -s -c "$admin" -X POST $GATEWAY/api/identity/login -H 'content-type: application/json' -d '{"email":"admin@3commerce.local","password":"dev-admin-password-1"}' >/dev/null
  check "L9a customer → 403 on admin" "403" bash -c "curl -s -o /dev/null -w '%{http_code}' -b '$jar' -X POST $GATEWAY/api/catalog/admin/import-runs"
  local imp; imp="$(curl -s -b "$admin" -X POST $GATEWAY/api/catalog/admin/import-runs)"
  local acc rej; acc="$(grep -oE '"accepted":[0-9]+' <<<"$imp" | grep -oE '[0-9]+')"; rej="$(grep -oE '"rejected":[0-9]+' <<<"$imp" | grep -oE '[0-9]+')"
  # Count is configurable (Importer:TargetRows); just require it worked. The 10k+rejection
  # scale is asserted by the CatalogSearchTests integration test (FR-1).
  { [[ "${acc:-0}" -gt 0 ]] && pass "L10 import (${acc} accepted/${rej:-0} rejected)"; } || fail "L10 import (acc=${acc:-?} rej=${rej:-?})"
  check "L11a exact search has total" "X-Total-Count" bash -c "curl -s -D - -o /dev/null '$GATEWAY/api/catalog/products?q=Headphones&pageSize=3'"
  check "L11b typo fallback" "Headphones" bash -c "curl -s '$GATEWAY/api/catalog/products?q=hedphones&pageSize=3'"
  check "L11c category+attr filter ok" "200" bash -c "curl -s -o /dev/null -w '%{http_code}' '$GATEWAY/api/catalog/products?category=audio&attrs=color:black'"
  local slug; slug="$(curl -s "$GATEWAY/api/catalog/products?q=Speaker&pageSize=1" | grep -oE '"slug":"[^"]+"' | head -1 | cut -d'"' -f4)"
  check "L11d product detail" "variants" bash -c "curl -s '$GATEWAY/api/catalog/products/$slug'"
  local p95; p95="$(for i in $(seq 1 30); do curl -s -o /dev/null -w '%{time_total}\n' "$GATEWAY/api/catalog/products?q=wireless+speaker&page=$i"; done | sort -n | awk '{a[NR]=$1} END{print a[int(NR*0.95)]}')"
  awk "BEGIN{exit !($p95 < 0.5)}" && pass "L12 search p95 ${p95}s < 0.5s" || fail "L12 search p95 ${p95}s"

  stage "L13  Logout + password reset"
  check "L13a logout → 204" "204" bash -c "curl -s -o /dev/null -w '%{http_code}' -b '$jar' -X POST $GATEWAY/api/identity/logout"
  curl -s -X POST $GATEWAY/api/identity/password-reset/request -H 'content-type: application/json' -d "{\"email\":\"$email\"}" >/dev/null
  sleep 3
  local rt; rt="$(grep -aoE 'reset-password\?token=[A-Za-z0-9_-]+' "$ROOT/.run/notifications.log" | tail -1 | cut -d= -f2)"
  curl -s -X POST $GATEWAY/api/identity/password-reset/confirm -H 'content-type: application/json' -d "{\"token\":\"$rt\",\"newPassword\":\"brand-new-password-9\"}" >/dev/null
  check "L13b login with new password" "200" bash -c \
    "curl -s -o /dev/null -w '%{http_code}' -X POST $GATEWAY/api/identity/login -H 'content-type: application/json' -d '{\"email\":\"$email\",\"password\":\"brand-new-password-9\"}'"

  stage "Seeding demo data (--profile full)"
  # Seed AFTER the L5–L13 auth/catalog smoke, not before it. Those checks need no demo data — and the seed
  # drives all 13 services hard (hundreds of registrations/logins/orders → an outbox + DB-connection burst),
  # which on a 2-vCPU CI runner transiently saturates the stack. Run before L5, that burst was still draining
  # when the rapid-fire auth checks fired, so register/login intermittently timed out and L5–L13 (+ the
  # admin-jar-dependent L10/L19) failed — while the same checks pass on a quiescent stack. Seeding here lets
  # L1–L13 run clean, then a settle drains the burst before the storefront/money/L20 stages that DO need the
  # demo data (multi-currency storefronts, Demo Supplier, scenario products, attributed orders/ledger, and
  # .run/dev-dummy-data/fixtures.json). The background storefront build (started above) finishes during it.
  if (( ! STACK_OWNED )) && [[ -s "$ROOT/.run/dev-dummy-data/fixtures.json" ]]; then
    # A REUSED stack that is already seeded keeps its data and its manifest. Re-seeding it deleted the
    # manifest the running stack was seeded with (and the demo logins in it) and piled a second full dataset
    # on top, whose projections were still draining when L20 started: the admin money/ledger specs then
    # raced it (5 failures that all passed on the settled stack). Specs read the existing, settled manifest.
    pass "Seed full demo data (reused stack already seeded — kept its data and manifest)"
  else
    rm -rf "$ROOT/.run/dev-dummy-data"   # never let a stale manifest from a prior run drive the specs
    if GATEWAY="$GATEWAY" "$ROOT/scripts/dev-dummy-data.sh" --profile full --gateway "$GATEWAY" >/tmp/3c-seed.log 2>&1; then
      pass "Seed full demo data ($(grep -oE 'step classifications:.*' /tmp/3c-seed.log | tail -1))"
    else
      fail "Seed full demo data"; tail -25 /tmp/3c-seed.log
    fi
  fi
  # Settle: let the seed's outbox/projection burst drain and every service report ready again before the
  # storefront + money-flow stages read the just-seeded state (avoids a post-seed saturation false-negative).
  local settle_ok=1; for p in 5101 5102 5103 5104 5105 5106 5107; do wait_health "$p" || settle_ok=0; done
  [[ $settle_ok == 1 ]] && echo "  services healthy after seed" || echo "  WARNING: a service was slow to re-report ready after seed"
  sleep 10

  stage "L14  Storefront SSR"
  wait_http "$STOREFRONT/" || fail "L14 storefront did not come up"
  # rev_5/F5: the bare root lists nothing until a storefront is pinned (locally via a /{slug} landing that
  # sets the 3c_storefront cookie; in prod by Host). Pin the first demo store the public config resolves,
  # then browse WITH that cookie against the store's OWN published catalog. When no demo storefront is
  # published (e.g. an import-only seed), skip the SSR product checks rather than fail on an empty root.
  local sfjar=/tmp/3c-e2e-sf.txt; rm -f "$sfjar"; local sfslug="" sfid="" sfcur="" sfship=""
  for s in au eu us; do
    local cfg; cfg="$(curl -fsS "$GATEWAY/api/catalog/storefronts/public?slug=$s" 2>/dev/null)" || continue
    if [[ -n "$cfg" ]]; then
      sfid="$(grep -oE '"id":"[^"]+"' <<<"$cfg" | head -1 | cut -d'"' -f4)"
      # The store's currency and a destination it serves: the money flow (L15/L16) shops on THIS store like a
      # real shopper, since checkout refuses a cart in another currency or not sellable there (ADR-0059).
      # shipTo = the first allowlisted country, or the currency's home country when the store ships worldwide.
      read -r sfcur sfship < <(python3 -c '
import json, sys
s = json.load(sys.stdin)
cur = s.get("currency") or "EUR"
home = {"AUD": "AU", "EUR": "DE", "USD": "US", "CAD": "CA", "GBP": "GB", "CNY": "CN", "JPY": "JP", "KWD": "KW"}
print(cur, (s.get("shipToCountries") or [None])[0] or home.get(cur, "AU"))' <<<"$cfg" 2>/dev/null)
      curl -s -c "$sfjar" "$STOREFRONT/$s" >/dev/null; sfslug="$s"; break
    fi
  done
  if [[ -n "$sfslug" ]]; then
    # Derive the product slug from THIS store's home so the PDP check uses a product it actually publishes.
    local sfprod; sfprod="$(curl -fsS -b "$sfjar" "$STOREFRONT/" | grep -oE 'href="/products/[^"]+"' | head -1 | sed -E 's#href="/products/([^"]+)"#\1#')"
    check "L14a home renders products" "</h3>" bash -c "curl -fsS -b '$sfjar' $STOREFRONT/"
    check "L14b search renders" "/products/" bash -c "curl -fsS -b '$sfjar' '$STOREFRONT/search'"
    check "L14c product detail renders" "<h1" bash -c "curl -fsS -b '$sfjar' '$STOREFRONT/products/$sfprod'"
  else
    skip "L14a-c storefront SSR — no demo storefront published (needs --data full)"
  fi
  check "L14d account redirects unauth" "307" bash -c "curl -s -o /dev/null -w '%{http_code}' $STOREFRONT/account"

  stage "L15-L19  Money flow: cart → checkout saga → ledger → refund"
  pay_scalar() { docker exec 3commerce-postgres psql -U payments_svc -d payments_db -tAc "$1" 2>/dev/null | tr -d '[:space:]'; }
  # Tables live in each service's named schema (ADR-0022), and the service role's search_path
  # ("$user",public) does not include it — so every direct psql query must schema-qualify.
  local trialbal='SELECT COALESCE(sum("DebitMinor"),0)-COALESCE(sum("CreditMinor"),0) FROM payments."JournalLines"'
  # Shop like a real shopper on the demo store L14 pinned: a product SELLABLE there, taken from the store's
  # own listing (its published catalogue in its currency, the same per-store/per-currency offer gate checkout
  # applies, ADR-0059), added to the cart IN the store's currency and shipped to a country it serves. A
  # product picked blind from Ordering's projection is arbitrary relative to the store, and checkout
  # (correctly) refuses it: wrong currency (checkoutBlock=1) or not sellable there (checkoutBlock=2).
  local prod=""
  if [[ -n "$sfid" && -n "$sfcur" ]]; then
    prod="$(curl -fsS "$GATEWAY/api/catalog/products?storefrontId=$sfid&currency=$sfcur&pageSize=1" 2>/dev/null \
      | python3 -c 'import json, sys; h = json.load(sys.stdin); print(h[0]["id"] if h else "")' 2>/dev/null)"
  fi
  local cartjar=/tmp/3c-e2e-cart.txt; rm -f "$cartjar"
  # Every order must belong to a storefront (checkout now rejects the synthetic default), so the money
  # flow needs a real demo store to attribute to; skip when none is published (import-only stack).
  if [[ -n "$prod" && -n "$sfid" ]]; then
    local addbody=/tmp/3c-e2e-cart-add.json addcode
    addcode="$(curl -s -o "$addbody" -w '%{http_code}' -c "$cartjar" -X POST $GATEWAY/api/ordering/cart/items -H 'content-type: application/json' -d "{\"productId\":\"$prod\",\"quantity\":2,\"currency\":\"$sfcur\"}")"
    [[ "$addcode" == "200" ]] && pass "L15 add to cart ($sfslug store, $sfcur)" || fail "L15 add to cart (HTTP $addcode: $(head -c 300 "$addbody"))"

    local shipjson; shipjson="$(python3 -c '
import json, sys
c = sys.argv[1]
city, pc = {"AU": ("Melbourne", "3000"), "DE": ("Berlin", "10115"), "US": ("New York", "10001")}.get(c, ("Capital", "1000"))
print(json.dumps({"name": "E", "line1": "1 St", "city": city, "postcode": pc, "country": c}))' "$sfship")"
    local cobody=/tmp/3c-e2e-checkout.json cocode co
    cocode="$(curl -s -o "$cobody" -w '%{http_code}' -b "$cartjar" -X POST $GATEWAY/api/ordering/checkout -H 'content-type: application/json' -d "{\"email\":\"e2e@example.com\",\"storefrontId\":\"$sfid\",\"shippingAddress\":$shipjson}")"
    co="$(cat "$cobody" 2>/dev/null)"
    local oid gross secret
    oid="$(grep -oE '"orderId":"[^"]+"' <<<"$co" | cut -d'"' -f4)"
    gross="$(grep -oE '"grossMinor":[0-9]+' <<<"$co" | grep -oE '[0-9]+')"
    secret="$(grep -oE '"clientSecret":"pi_fake_[^"]+"' <<<"$co")"
    # A refusal prints its HTTP status + problem+json body, so it is diagnosable from the log alone.
    { [[ -n "$oid" && -n "$secret" && "${gross:-0}" -gt 0 ]] && pass "L16 checkout (gross=$gross $sfcur, ship to $sfship, intent returned)"; } \
      || fail "L16 checkout on $sfslug ($sfcur, ship to $sfship) -> HTTP $cocode: $(head -c 400 <<<"$co")"

    # Wait for the saga to start, then simulate the payment.
    sleep 3
    local intent="pi_fake_$(tr -d - <<<"$oid")"
    curl -s -o /dev/null -X POST "localhost:5104/dev/simulate-payment/$intent"
    local confirmed=0
    for _ in $(seq 1 15); do
      [[ "$(curl -s $GATEWAY/api/ordering/orders/$oid/status | grep -oE '"status":"[^"]+"' | cut -d'"' -f4)" == "Confirmed" ]] && { confirmed=1; break; }; sleep 2
    done
    [[ $confirmed == 1 ]] && pass "L17 saga confirms order" || fail "L17 saga confirm"

    # L17b: OrderConfirmed reached EVERY subscriber — Fulfillment recorded the order (a shipment, or a hold that
    # defers it) AND Notifications emailed the confirmation. Two services whose consumers share a queue name
    # (same consumer class name, ADR-0060) become competing consumers and each order reaches only ONE of them.
    local fulfilled=0 emailed=0
    for _ in $(seq 1 15); do
      [[ $fulfilled == 1 ]] || { [[ "$(curl -s -b "$admin" "$GATEWAY/api/fulfillment/admin/shipments?orderId=$oid")$(curl -s -b "$admin" "$GATEWAY/api/fulfillment/admin/orders/$oid/holds")" == *'"id"'* ]] && fulfilled=1; }
      [[ $emailed == 1 ]] || { grep -aq "Order $oid for" "$ROOT/.run/notifications.log" 2>/dev/null && emailed=1; }
      [[ $fulfilled == 1 && $emailed == 1 ]] && break; sleep 2
    done
    { [[ $fulfilled == 1 && $emailed == 1 ]] && pass "L17b confirmed order reached Fulfillment AND Notifications"; } \
      || fail "L17b OrderConfirmed fan-out (fulfillment=$fulfilled email=$emailed) — a queue shared by two services? ADR-0060"

    local saleTb; saleTb="$(pay_scalar "$trialbal")"
    { [[ "$saleTb" == "0" ]] && pass "L18 ledger balanced after sale"; } || fail "L18 trial balance=$saleTb"

    # Unique key per order — a fixed key would (correctly) dedupe across re-runs on a persistent DB.
    curl -s -o /dev/null -b "$admin" -X POST $GATEWAY/api/payments/admin/refunds -H 'content-type: application/json' -H "Idempotency-Key: e2e-refund-$oid" -d "{\"orderId\":\"$oid\",\"amountMinor\":$gross,\"reason\":\"e2e\"}"
    # Poll for the refund saga (RefundRequested → ExecuteRefundConsumer → Refunds row + reversal): a fixed
    # sleep is too tight on a loaded CI runner, so wait up to ~30s for the row to land instead of racing it.
    local refunded=0 refTb
    for _ in $(seq 1 15); do
      refunded="$(pay_scalar "SELECT count(*) FROM payments.\"Refunds\" WHERE \"OrderId\"='$oid'")"
      [[ "${refunded:-0}" -ge 1 ]] && break; sleep 2
    done
    refTb="$(pay_scalar "$trialbal")"
    { [[ "$refTb" == "0" && "${refunded:-0}" -ge 1 ]] && pass "L19 refund reverses, ledger balanced"; } || fail "L19 refund (tb=$refTb refunds=$refunded)"
  elif [[ -n "$sfid" ]]; then
    fail "L15-L19 demo storefront '$sfslug' lists no sellable product in ${sfcur:-its currency}"
  else
    skip "L15-L19 money flow — no demo storefront to attribute the order (needs --data full)"
  fi

  # The list reporter's closing tally, e.g. "2 flaky, 4 skipped, 107 passed". Playwright counts a flaky
  # test (failed, then passed on retry) as green, so report it instead of only "N passed".
  pw_tally() {
    local t
    t=$(grep -E '^  [0-9]+ (passed|failed|flaky|skipped|interrupted|did not run)' /tmp/3c-playwright.log \
      | sed -E 's/^ +//; s/ \(.*\)$//' | paste -sd, - | sed 's/,/, /g')
    echo "${t:-no Playwright summary}"
  }
  pw_flaky_names() {
    awk '/^  [0-9]+ flaky/ {f=1; next} f && /^    / {sub(/^ +/, ""); print "    flaky: " $0; next} {f=0}' /tmp/3c-playwright.log
  }
  stage "L20  Storefront + Admin E2E (Playwright, real browser)"
  if [[ -d "$ROOT/src/Storefront/node_modules/@playwright" ]]; then
    wait_http "http://localhost:5200/login" || true  # ensure admin is up
    wait_http "http://localhost:5300/login" || true  # ensure supplier portal is up
    if ( cd "$ROOT/src/Storefront" && STOREFRONT_URL="$STOREFRONT" ADMIN_URL="http://localhost:5200" SUPPLIER_URL="http://localhost:5300" GATEWAY_URL="$GATEWAY" npx playwright test >/tmp/3c-playwright.log 2>&1 ); then
      pass "L20 storefront + admin E2E ($(pw_tally))"
      # CI retries (playwright.config.ts) let a starved test pass on its 2nd try: name every flaky one.
      pw_flaky_names
    else
      fail "L20 E2E ($(pw_tally))"; grep -E 'passed|failed|✘|›' /tmp/3c-playwright.log | tail -8
      # .run/ in both modes: a reused stack's frontends were started by dev-up.sh, which logs there too.
      echo "--- admin log ---"; tail -25 "$ROOT/.run/admin.log" 2>/dev/null
      echo "--- storefront log ---"; tail -10 "$ROOT/.run/storefront.log" 2>/dev/null
    fi
  else
    echo "  (skipped: Playwright not installed — cd src/Storefront && npm i && npx playwright install chromium)"
  fi

  live_teardown
  trap - INT TERM
}

# ── Run ──────────────────────────────────────────────────────────────────────
printf '\033[1m3commerce E2E verification — mode: %s\033[0m\n' "$MODE"
[[ "$MODE" != "live" ]] && run_automated
[[ "$MODE" == *live* ]] && run_live

stage "Summary"
printf '  passed: %d   failed: %d\n' "$PASS" "$FAIL"
if (( FAIL > 0 )); then
  printf '\n  failing checks:\n'; printf '    - %s\n' "${FAILED[@]}"
  exit 1
fi
printf '\n  \033[32mAll checks passed.\033[0m\n'
