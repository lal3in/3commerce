# Storefront operations

The customer-facing Next.js app at **`http://localhost:3000`**. Every page is a
Server Component rendered on the server (SSR/ISR); all state-changing actions are
**Server Actions** in `src/Storefront/lib/*-actions.ts`, which call the YARP
gateway and (where needed) relay cookies. The browser never sees the gateway URL
or session token directly.

## Global chrome (every page)

The root layout (`app/layout.tsx`) renders a header on every page:

- **3commerce** logo → `/` (home)
- A **search box** (`name="q"`) that submits `GET /search`
- Nav links: **Shop** → `/search`, **Cart** → `/cart`, **Account** → `/account`
- A footer ("3commerce — demo storefront")

Money everywhere is formatted by `lib/money.ts` (`formatMoney(minorUnits, currency)`),
i.e. integer cents rendered via `Intl.NumberFormat` (en-US locale, so `A$`/`$`/`€`
stay unambiguous). A cookie **consent banner** appears until the shopper decides,
linking to the [privacy settings page](#privacy) at `/privacy`.

---

## Storefront context — which store am I in?

One Next.js app serves **multiple storefronts**. Every shopper route resolves its
storefront context, which drives the displayed **currency** and the **tax regime**:

1. Visiting a storefront slug as a path — e.g. **`/au`** — makes `middleware.ts` pin
   that storefront in a **`3c_storefront` cookie** (entering another slug switches it).
2. `resolveStorefront()` (`lib/storefront-context.ts`, cached per request) resolves
   **cookie → canonical host → `STOREFRONT_SLUG` env** and fetches the public config
   via `GET /api/catalog/storefronts/public` (anonymous, active storefronts only:
   name, currency, tax regime/rate, storefront + tenant ids).
3. Home, search, product detail, and add-to-cart are then priced in the storefront's
   currency; checkout forwards the storefront/tenant context (`X-3C-Tenant-Id` /
   storefront id) so the server charges tax by the same storefront config.

**Per-currency prices** are tenant-set per variant (unique per currency). A product
with **no price in the storefront's currency is hidden** from listings/search and its
detail returns 404 in that context — no silent FX conversion. With no cookie/host
match, the default context applies (dev default currency `EUR`).

**Tax display convention (ADR-0038):** AU GST and EU VAT storefronts show
**tax-inclusive** shelf prices — checkout charges exactly the listed amount and
reports the contained tax ("Includes tax (10%)"). US-style storefronts show
tax-**exclusive** prices and add tax at checkout ("Tax (added)").

---

## 1. Home — `/`

File: `app/page.tsx`. Server-rendered. On load it fetches, in parallel:

- featured products — `searchProducts({ pageSize: 8 })` → `GET /api/catalog/products?...`
- categories — `listCategories()` → `GET /api/catalog/categories`

Sections shown:

1. A hero banner ("Everything, sourced for you.") with a **Start shopping** button → `/search`.
2. **Categories** — chips, each linking to `/search?category=<slug>` (hidden if none).
3. **Featured** — a product grid of up to 8 items.

> If the catalog has not been imported yet, the grid and categories are empty —
> run the sample importer from the admin first (see [Admin operations](./admin-operations.md)).

Visiting a storefront slug (e.g. `/au`) renders the same landing page in that
storefront's context (`app/[storefront]/page.tsx`): its name, currency, and a tax
summary line (e.g. "AU GST (10%)"), with prices in that currency. Only **Active**
products with a price in the storefront currency appear; inactive products are
excluded from public listings, search, and detail (they remain editable in the
admin catalog). Product images from non-allow-listed hosts degrade to a plain
image instead of failing the page (`components/SafeImage`, `lib/image-hosts.ts`).

---

## 2. Search — `/search`

File: `app/search/page.tsx`. The URL **is** the state (shareable + crawlable).
Query params: `q`, `category`, `attrs`, `page`. Page size is 24.

Steps:

1. Type in the header search box and press Enter → navigates to `/search?q=<term>`.
   (Or click a category chip → `/search?category=<slug>`.)
2. The page calls `searchProducts(...)` → `GET /api/catalog/products?q=...&category=...&attrs=...&page=...&pageSize=24&currency=<storefront currency>`.
   Results are priced in the storefront currency; products without a price in it —
   and products whose status is Inactive — are not returned.
3. Results render as a product grid with a heading
   (`Results for "<q>"`, or `Category: <slug>`, or `All products`) and an item count
   read from the `X-Total-Count` response header.
4. **Pagination** appears when there is more than one page: **Previous** / **Next**
   links that preserve `q`/`category`/`attrs` and bump `page`.

### Typo tolerance

Search is typo-tolerant on the backend (Postgres FTS + `pg_trgm` trigram fallback,
ADR-0020). The storefront does nothing special — it just forwards `q`. Example:
searching `hedphones` still surfaces "Headphones". This is exercised by the
`browse.spec.ts` E2E test and the catalog integration tests.

---

## 3. Product detail — `/products/[slug]`

File: `app/products/[slug]/page.tsx`. **ISR** — `getProduct(slug)` fetches
`GET /api/catalog/products/<slug>` with `next: { revalidate: 300 }` (5-minute
cache). Unknown slugs render `notFound()` (404).

What you see:

- Product image (Next `<Image>`; dev images come from `picsum.photos`).
- Brand, title, a "from" price (the minimum variant price), and description.
- **Options** — a list of variants: SKU, price, and **In stock / Out of stock**.
- **Attributes** — a key/value grid (only if the product has attributes).
- **Add to cart** button.

### Add to cart

The **Add to cart** button (`components/cart/AddToCartButton.tsx`) is a client leaf:

1. Click → runs the `addToCart(productId)` Server Action (`lib/cart-actions.ts`).
2. The action `POST`s to `/api/ordering/cart/items` with `{ productId, quantity: 1 }`,
   forwarding the `3c_cart` and `3c_session` cookies.
3. It **relays the cart cookie**: if the gateway returns a `Set-Cookie: 3c_cart=...`,
   the action writes it back as an HttpOnly cookie (the cart is cookie-keyed).
4. `revalidatePath("/cart")` runs, then the button navigates you to `/cart`.

---

## 4. Cart — `/cart`

File: `app/cart/page.tsx`. Dynamic, never cached. Reads the cart via
`getCart()` → `GET /api/ordering/cart/`.

- **Empty cart:** shows "Your cart is empty" with a **Browse products** link → `/search`.
- **With items:** lists each line (`CartItemRow`) with image, title, unit price, and
  quantity; a **Subtotal**; the note "Shipping and tax are calculated at checkout";
  and a **Checkout** button → `/checkout`.

Carts are **single-currency**: items are priced in the storefront currency at
add-to-cart time, and adding an item in a different currency is rejected (409).
Logging in merges the anonymous cart into the user cart, re-pricing lines into the
user cart's currency (unpriced lines are dropped).

> A read-only cart render never sets cookies. An un-keyed visitor (no `3c_cart`
> cookie yet) just sees an empty cart — the cookie is established by the
> add-to-cart action.

Removing an item uses the `removeFromCart(productId)` Server Action →
`DELETE /api/ordering/cart/items/<productId>`, then revalidates `/cart`.

---

## 5. Checkout (guest) — `/checkout`

File: `app/checkout/page.tsx` + `components/checkout/CheckoutForm.tsx`.
Guest checkout — email + shipping only, no account required (ADR-0013). If the
cart is empty the page redirects to `/cart`.

Steps:

1. The page shows an order summary: **Subtotal**, **Shipping** (once a rate is
   chosen), and a tax line that follows the storefront's regime (ADR-0038) —
   **"Includes tax (10%)"** on tax-inclusive storefronts (AU GST / EU VAT: the tax
   is informational, already contained in the listed prices) or **"Tax (added)"**
   on exclusive ones (US style: tax is added to the total). The server charges by
   the same storefront tax config — the gross always matches what the page shows.
2. Fill the form: **Email**, **Full name**, **Address**, **City**, **Postcode**,
   **Country (2-letter)** — all required.
3. Click **Get shipping rates**. The `quoteCheckoutShipping` Server Action calls
   `/api/fulfillment/shipping/quote` through the gateway with a default dev parcel
   and warehouse origin. The returned Fake/sandbox carrier rates are rendered as
   radio options.
4. Select a shipping method. The selected service, amount in minor units, and
   quote expiry are posted with checkout.
5. Pick a **payment option**: Credit card, Stripe Payment Element, **Apple Pay**,
   **Google Pay**, or **PayPal**. Wallets are payment *methods* tokenized through
   the storefront's payment provider, not separate providers (ADR-0039). In
   production these are provider-hosted surfaces — raw payment credentials are
   never stored by 3commerce; the dev card fields record only a masked summary.
6. Click **Authorize & place order** (button shows "Authorizing…" while pending).
7. The `submitCheckout` Server Action (`lib/cart-actions.ts`) `POST`s to
   `/api/ordering/checkout` with the email, shipping address, selected shipping
   quote, and payment option (cart cookie forwarded; storefront/tenant context
   headers set). Ordering validates the selected amount/expiry and persists the
   shipping amount into the order totals.
8. On success it redirects to `/checkout/confirmation?order=<orderId>`.
   On failure the form shows: "Checkout failed. Please review your cart and details."

The backend creates the order, starts the checkout saga, and asks Payments to
authorize. Which rail runs depends on the resolved **payment mode** (ADR-0039):
in dev, `Payments:Mode=LocalMock` routes authorization to a deterministic mock
provider (no external calls) — payment is completed on the confirmation page.

---

## 6. Confirmation — `/checkout/confirmation`

File: `app/checkout/confirmation/page.tsx` + `components/checkout/ConfirmationView.tsx`.
Requires an `?order=<id>` param (otherwise: "No order specified.").

Behaviour ("pending-first", then poll):

1. The server reads the initial status via `getOrderStatus(order)` →
   `GET /api/ordering/orders/<id>/status` (defaults to `AwaitingPayment`).
2. The client view **polls every 2 seconds** at `/api/order-status/<orderId>`
   (a thin Next route proxy, `app/api/order-status/[orderId]/route.ts`) until the
   status is `Confirmed` or `Cancelled`.
3. **While pending** it shows "Completing your payment…" and, in dev, a
   **Complete test payment (dev)** button.
   - Clicking it `POST`s to `/api/dev-pay/<orderId>`
     (`app/api/dev-pay/[orderId]/route.ts`), which builds the fake intent id
     `pi_fake_<orderId-without-dashes>` and calls
     `POST /api/payments/dev/simulate-payment/<intentId>` through the gateway.
     The Payments service only honours this in its Development environment.
4. **Confirmed** → "Thank you!" with a "Track your order / Set a password" card
   linking to `/register`.
5. **Cancelled** → "Payment not completed" with a **Return to cart** link.

> The dev button exists because dev runs in **LocalMock** payment mode (ADR-0039):
> the mock provider issues `pi_fake_…` intents and, with `Payments:AllowMockEmail`
> on, every mock authorize/refund also emails a clearly-labelled
> **`[TEST ONLY / MOCK PAYMENT]`** capture (redacted payload — never card data) to
> `Payments:MockEmailTo` for inspection. In Sandbox/Production modes the real
> provider's hosted flow + `/webhooks/{provider}` path drives the status instead,
> and the mock/email path **refuses to boot** outside Development.

---

## 7. Register — `/register`

Files: `app/register/page.tsx` + `app/register/RegisterForm.tsx`.

1. Enter **Email** and **Password (min 10 characters)** (`minLength={10}` enforced client-side).
2. Click **Create account**.
3. The `register` Server Action (`lib/auth-actions.ts`) `POST`s to
   `/api/identity/register` with `{ email, password }`.
4. On success it redirects to `/login?registered=1`, which shows a green banner:
   "Account created. Check your email to verify, then log in."
   On failure: "Could not register. Try a different email or a longer password."

> Registration does **not** auto-login; the response is intentionally identical on
> repeat (no user enumeration). The verification email is delivered by the
> Notifications worker.

---

## 8. Login / Logout — `/login`, `/account`

**Login** (`app/login/page.tsx` + `LoginForm.tsx`):

1. Enter **Email** + **Password**, click **Log in**.
2. The `login` Server Action `POST`s to `/api/identity/login`.
3. If the account has **MFA enrolled**, the response is `{ mfaRequired }` and the
   form shows an **Authenticator code** input (recovery codes work too); submitting
   completes the challenge via `/api/identity/mfa/challenge`. Wrong codes count
   toward the password lockout.
4. On success it **forwards the gateway's `Set-Cookie: 3c_session=...`** to the
   browser as an HttpOnly cookie, then redirects to `/account`.
   On failure: "Invalid email or password."
5. A **Register** link sits below the form.

**Logout** is a Server Action triggered by the **Log out** button on `/account`:
it `POST`s to `/api/identity/logout` with the session cookie, deletes the
`3c_session` cookie, and redirects to `/`.

---

## 9. Account — `/account`

File: `app/account/page.tsx`. Dynamic, cookie-dependent, never cached.

1. The page calls `getProfile()` → `GET /api/identity/me`. If there is no valid
   session it **redirects to `/login`** (the unauth case returns HTTP 307).
2. Otherwise it shows **Email** and **Email verified (Yes / Pending)**, plus a
   **Log out** button.

The signed-in account page shows the customer profile, saved address book,
saved cards, and order history. Each order row links to `/orders/<id>/support`
for support/RMA requests.

**Guest orders attach by verified email (FR-7).** Orders placed as a guest with
your email appear in the history once the email is **verified** — this works both
ways: verifying sweeps existing guest orders, and orders created *after*
verification attach at creation (Ordering keeps a verified-customer read model).
While verification is pending the page shows an amber notice: "Verify your email
address (…) — orders you placed as a guest with that email will appear here once
it's verified."

---

## 10. Order support — `/orders/[id]/support`

Files: `app/orders/[id]/support/page.tsx` + `components/support/SupportForms.tsx`.
The page renders two forms for a given order id (and shows a green "Your request
was submitted…" banner when arriving with `?submitted=1`).

### Report a problem (support ticket)

1. Fill **Your email**, pick a **reason**, and write a message.
   Reasons (typed, ADR-0018): `1` Where is my order?, `2` Arrived damaged,
   `3` Refund request, `4` Other.
2. Click **Open ticket**.
3. The `openTicket` Server Action (`lib/support-actions.ts`) `POST`s to
   `/api/support/tickets` with `{ orderId, email, reason, message }` (session
   cookie forwarded).
4. On success the form shows "Ticket opened." On failure: "Could not open the ticket."

### Request a refund (RMA)

1. Review the order lines available for return/refund.
2. Select the line(s) and quantities to request, then enter the customer email and reason.
3. The `requestRefund` Server Action `POST`s to `/api/support/rma` with the order id,
   email, reason, and selected lines — the server derives the refundable amount from its
   order snapshot instead of trusting a free-form client amount.
4. On success it redirects to `/orders/<id>/support?submitted=1` (green banner).
   On failure: "Could not submit the refund request."

This opens an RMA in the **Requested** state. An operator then approves and refunds
it from the admin **RMA queue** (see [Admin operations](./admin-operations.md)) —
the same refund path used everywhere, balancing the double-entry ledger.

The account order history and confirmation journey link shoppers toward support/RMA
rather than requiring direct URL entry.

---

## 11. Privacy & consent — `/privacy` <a id="privacy"></a>

Files: `app/privacy/page.tsx` + `app/privacy/ConsentSettings.tsx`; consent state in
`lib/consent.ts`. The permanent home for cookie/consent choices (the consent banner
links here). Necessary cookies are always on; **analytics and marketing are
optional, off by default**, and can be changed at any time. Withdrawing analytics
consent also **deletes the first-party visitor id** from the browser.

### Analytics collection (consent-aware)

`lib/analytics.ts` exposes `track(eventType, payload)` — a **no-op without
Analytics consent**. Consented events are batched and posted to the storefront's
own `/api/analytics/events` route, which proxies them to the Marketing service
(`POST /api/marketing/events`, tenant resolved from the storefront context) and
always answers 202 — analytics can never disrupt a page. Server-side the collector
deduplicates by event id, keeps only a coarse IP, drops sensitive payload keys, and
caps batches at 50 events.

---

## Server Action → gateway endpoint reference

| Action (file) | Calls | Notes |
|---------------|-------|-------|
| `addToCart` (`cart-actions.ts`) | `POST /api/ordering/cart/items` | Relays `3c_cart` cookie; revalidates `/cart`. |
| `removeFromCart` (`cart-actions.ts`) | `DELETE /api/ordering/cart/items/<id>` | Revalidates `/cart`. |
| `quoteCheckoutShipping` (`cart-actions.ts`) | `POST /api/fulfillment/shipping/quote` | Fetches checkout shipping rates before authorization. |
| `submitCheckout` (`cart-actions.ts`) | `POST /api/ordering/checkout` | Posts selected shipping quote + address; redirects to confirmation. |
| `login` (`auth-actions.ts`) | `POST /api/identity/login` (+ `POST /api/identity/mfa/challenge` when enrolled) | Forwards `3c_session` cookie; redirects to `/account`. |
| `register` (`auth-actions.ts`) | `POST /api/identity/register` | Redirects to `/login?registered=1`. |
| `logout` (`auth-actions.ts`) | `POST /api/identity/logout` | Deletes cookie; redirects to `/`. |
| `openTicket` (`support-actions.ts`) | `POST /api/support/tickets` | Returns `{ ok }` / `{ error }`. |
| `requestRefund` (`support-actions.ts`) | `POST /api/support/rma` | Posts selected order lines; server derives refund amount; redirects with `?submitted=1`. |
| (read) `getCart`, `searchProducts`, `getProduct`, `listCategories`, `getProfile`, `getOrderStatus` (`gateway.ts`) | `GET /api/...` | Server-side reads, session/cart cookies forwarded. |
| dev-pay route | `POST /api/payments/dev/simulate-payment/<intent>` | Dev-only mock payment (LocalMock mode). |
| order-status route | `GET /api/ordering/orders/<id>/status` | Polling proxy. |
| analytics route (`app/api/analytics/events`) | `POST /api/marketing/events` | Consent-gated batch proxy; always 202. |
