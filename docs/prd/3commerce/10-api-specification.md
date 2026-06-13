# 10. API Specification

All public traffic goes through the YARP gateway at a single origin. Convention: `/api/{service}/{resource}`. Auth column: 🔓 anonymous, 🔐 session cookie, 👑 admin role.

> This is the v1 surface contract — detailed request/response schemas live with the code (OpenAPI per service); examples below set the conventions.

## Identity — `/api/identity`

| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/register` | 🔓 | Create account (email, password) → sends verification email |
| POST | `/login` | 🔓 | Sets session cookie |
| POST | `/logout` | 🔐 | Revokes session |
| POST | `/verify-email` | 🔓 | Single-use token |
| POST | `/password-reset/request` · `/password-reset/confirm` | 🔓 | Reset flow; revokes all sessions on confirm |
| GET/PUT | `/me` | 🔐 | Profile |
| GET/POST/PUT/DELETE | `/me/addresses` | 🔐 | Saved addresses |
| POST | `/convert-guest` | 🔓 | Token from order email + password → account with orders attached |

*(Session introspection is internal-only: gateway ↔ Identity, not routed publicly.)*

## Catalog — `/api/catalog`

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/products?q=&category=&attrs=&page=` | 🔓 | Search (FTS + trigram) and filtered browse |
| GET | `/products/{slug}` | 🔓 | Product detail (SSR data source) |
| GET | `/categories` | 🔓 | Category tree |
| POST/PUT/DELETE | `/admin/products...` | 👑 | Catalog CRUD |
| GET | `/admin/import-runs` | 👑 | Import monitoring |
| POST | `/admin/import-runs` | 👑 | Trigger importer |

## Ordering — `/api/ordering`

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET/POST | `/cart` · `/cart/items` | 🔓 | Cart (anonymous via cart cookie; merged on login) |
| PUT/DELETE | `/cart/items/{id}` | 🔓 | Update/remove line |
| POST | `/checkout` | 🔓 | Starts checkout saga → returns order ID + Stripe client secret |
| GET | `/orders` | 🔐 | Order history |
| GET | `/orders/{id}` | 🔐* | Order detail (*guest access via signed link from email) |
| GET | `/admin/orders...` | 👑 | Order management views |

### Example: `POST /api/ordering/checkout`

```jsonc
// Request
{
  "email": "shopper@example.com",          // required for guests
  "shippingAddress": { "name": "...", "line1": "...", "city": "...", "country": "DE", "postcode": "..." }
}
// 201 Response
{
  "orderId": "0190a1b2-...",               // UUIDv7
  "status": "PaymentRequested",
  "payment": { "provider": "stripe", "clientSecret": "pi_..._secret_..." },
  "totals": { "currency": "EUR", "items": 4999, "shipping": 499, "tax": 950, "grand": 6448 }  // minor units
}
```

## Payments — `/api/payments`

| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/webhooks/stripe` | 🔓 (signature-verified) | Stripe events; idempotent by event ID |
| GET | `/admin/ledger/accounts` · `/admin/ledger/entries` | 👑 | Ledger inspection |
| POST | `/admin/refunds` | 👑 | Admin-initiated refund (also reachable via Support RMA saga) |
| GET | `/admin/xero/sync-runs` | 👑 | Xero sync status |

## Fulfillment — `/api/fulfillment`

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/admin/shipments?orderId=` | 👑 | Shipments grouped by per-line fulfillment source |
| POST | `/admin/shipments/{id}/tracking` | 👑 | Assign tracking → emits event → customer email |

## Support — `/api/support`

| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/tickets` | 🔐* | Open order-linked ticket (typed reason; *guests via signed order link) |
| GET | `/tickets` · `/tickets/{id}` | 🔐 | My tickets / thread |
| POST | `/tickets/{id}/messages` | 🔐 | Reply |
| POST | `/tickets/{id}/rma` | 🔐 | Request refund/return (creates RMA) |
| GET | `/admin/rmas?state=Requested` | 👑 | RMA queue |
| POST | `/admin/rmas/{id}/approve` · `/deny` | 👑 | Approve triggers refund saga; idempotent |

## Cross-cutting conventions

- **Errors:** RFC 7807 `application/problem+json` everywhere.
- **Money:** integer minor units + ISO 4217 currency code in every payload.
- **Idempotency:** mutating money endpoints accept an `Idempotency-Key` header.
- **Pagination:** `?page=&pageSize=` with `X-Total-Count`; max pageSize 100.
- **Versioning:** none in v1 (single first-party client); message contracts version additively.
- **Internal claims header** (`X-Internal-Claims` JWT) is gateway-minted and stripped from inbound public requests.
