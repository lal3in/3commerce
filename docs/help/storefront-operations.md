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
i.e. integer cents rendered via `Intl.NumberFormat`.

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

---

## 2. Search — `/search`

File: `app/search/page.tsx`. The URL **is** the state (shareable + crawlable).
Query params: `q`, `category`, `attrs`, `page`. Page size is 24.

Steps:

1. Type in the header search box and press Enter → navigates to `/search?q=<term>`.
   (Or click a category chip → `/search?category=<slug>`.)
2. The page calls `searchProducts(...)` → `GET /api/catalog/products?q=...&category=...&attrs=...&page=...&pageSize=24`.
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

1. The page shows an order summary (subtotal + item count) and tells the shopper
   to choose a shipping rate before authorization.
2. Fill the form: **Email**, **Full name**, **Address**, **City**, **Postcode**,
   **Country (2-letter)** — all required.
3. Click **Get shipping rates**. The `quoteCheckoutShipping` Server Action calls
   `/api/fulfillment/shipping/quote` through the gateway with a default dev parcel
   and warehouse origin. The returned Fake/sandbox carrier rates are rendered as
   radio options.
4. Select a shipping method. The selected service, amount in minor units, and
   quote expiry are posted with checkout.
5. Click **Authorize & place order** (button shows "Authorizing…" while pending).
6. The `submitCheckout` Server Action (`lib/cart-actions.ts`) `POST`s to
   `/api/ordering/checkout` with the email, shipping address, and selected shipping
   quote (cart cookie forwarded). Ordering validates the selected amount/expiry and
   persists the shipping amount into the order totals.
7. On success it redirects to `/checkout/confirmation?order=<orderId>`.
   On failure the form shows: "Checkout failed. Please review your cart and details."

The backend creates the order, starts the checkout saga, and (in dev) issues a
**fake** payment intent. The page does **not** collect card details — payment is
completed on the confirmation page.

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

> In production (real Stripe key set), the dev button would not apply — the
> Payment Element / webhook path would drive the status instead. There is no live
> Stripe in this build.

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
3. On success it **forwards the gateway's `Set-Cookie: 3c_session=...`** to the
   browser as an HttpOnly cookie, then redirects to `/account`.
   On failure: "Invalid email or password."
4. A **Register** link sits below the form.

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

1. Fill **Your email**, **Amount in cents** (`amountMinor`), and a free-text **Reason**.
2. Click **Request refund**.
3. The `requestRefund` Server Action `POST`s to `/api/support/rma` with
   `{ orderId, email, amountMinor, reason }`.
4. On success it redirects to `/orders/<id>/support?submitted=1` (green banner).
   On failure: "Could not submit the refund request."

This opens an RMA in the **Requested** state. An operator then approves and refunds
it from the admin **RMA queue** (see [Admin operations](./admin-operations.md)) —
the same refund path used everywhere, balancing the double-entry ledger.

> **Note:** there is no link from the order/account pages to this support page;
> you reach it by navigating directly to `/orders/<order-id>/support` (the
> confirmation page's "Track your order" card links to register, not to support).

---

## Server Action → gateway endpoint reference

| Action (file) | Calls | Notes |
|---------------|-------|-------|
| `addToCart` (`cart-actions.ts`) | `POST /api/ordering/cart/items` | Relays `3c_cart` cookie; revalidates `/cart`. |
| `removeFromCart` (`cart-actions.ts`) | `DELETE /api/ordering/cart/items/<id>` | Revalidates `/cart`. |
| `quoteCheckoutShipping` (`cart-actions.ts`) | `POST /api/fulfillment/shipping/quote` | Fetches checkout shipping rates before authorization. |
| `submitCheckout` (`cart-actions.ts`) | `POST /api/ordering/checkout` | Posts selected shipping quote + address; redirects to confirmation. |
| `login` (`auth-actions.ts`) | `POST /api/identity/login` | Forwards `3c_session` cookie; redirects to `/account`. |
| `register` (`auth-actions.ts`) | `POST /api/identity/register` | Redirects to `/login?registered=1`. |
| `logout` (`auth-actions.ts`) | `POST /api/identity/logout` | Deletes cookie; redirects to `/`. |
| `openTicket` (`support-actions.ts`) | `POST /api/support/tickets` | Returns `{ ok }` / `{ error }`. |
| `requestRefund` (`support-actions.ts`) | `POST /api/support/rma` | Redirects with `?submitted=1`. |
| (read) `getCart`, `searchProducts`, `getProduct`, `listCategories`, `getProfile`, `getOrderStatus` (`gateway.ts`) | `GET /api/...` | Server-side reads, session/cart cookies forwarded. |
| dev-pay route | `POST /api/payments/dev/simulate-payment/<intent>` | Dev-only fake payment. |
| order-status route | `GET /api/ordering/orders/<id>/status` | Polling proxy. |
