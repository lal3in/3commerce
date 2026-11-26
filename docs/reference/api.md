# API Endpoint Guidelines

Standards for building HTTP endpoints in the 13 **ASP.NET Core minimal API** services (`scripts/lib/services.sh`) behind the YARP gateway. Grounded in ADR-0006/0007/0011/0012 and the PRD API conventions (§10). Read this before adding or changing any endpoint.

---

## 1. Endpoint organization

- **No Program.cs dumping ground.** Each service organizes endpoints as one static class per resource in `Api/Endpoints/`, registered via `MapGroup`:

```csharp
// Api/Endpoints/CartEndpoints.cs
public static class CartEndpoints
{
    public static RouteGroupBuilder MapCart(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/cart").WithTags("Cart");
        group.MapGet("/", GetCart);
        group.MapPost("/items", AddItem);
        return group;
    }

    private static async Task<Results<Ok<CartDto>, NotFound>> GetCart(...) { ... }
}
```

- Handlers are **named private static methods** (testable, profilable) — never multi-line lambdas inline in the map call.
- Endpoints are thin: bind → authorize → call Domain → map result. **No business logic in handlers**; that lives in `Domain/`.
- Always accept and forward a `CancellationToken`.

## 2. Routing & naming (gateway contract)

- Public shape: `/api/{service}/{resource}` — the gateway strips `/api/{service}`; services define `/{resource}` locally. Plural nouns (`/products`, `/orders/{id}`), kebab-case path segments, no verbs in URLs (verbs belong to HTTP methods; saga triggers like `/rmas/{id}/approve` are the sanctioned exception).
- Admin endpoints live under `/admin/...` within each service; the route group requires `InternalClaimsAuth.AdminPolicy` (customer routes `CustomerPolicy`, supplier-portal routes `SupplierPolicy`), and sensitive actions additionally ask the Identity PDP for a permission (§6).
- Service-to-service endpoints live under `/internal/...`; the gateway never proxies `/api/{service}/internal…` from the public origin (`src/Gateway/Program.cs`), so never put a public route there.
- Route params bind to strongly-typed values (`Guid`, not `string`).

## 3. Requests, responses, status codes

- **DTOs are records**, defined in the service's `Api/` layer. Never expose EF entities or domain objects directly; map explicitly.
- Use **`TypedResults`** with `Results<T1, T2, ...>` unions so response types are compile-checked and flow into OpenAPI automatically.
- Status codes:
  - `200` reads · `201 + Location` creations · `204` deletes/no-content updates
  - `202`-semantics for saga starts: checkout returns `201` with the order in `PaymentRequested` state — **never block an HTTP request on saga completion** (ADR-0007)
  - `400` malformed **and** request validation failures (`TypedResults.ValidationProblem` and the built-in validation both answer 400 — that is the convention across the services) · `422` only for a well-formed request the domain refuses on business grounds (e.g. "that card is not one of your saved cards") · `401` unauthenticated · `403` unauthorized · `404` not-found-or-not-yours · `409` state conflicts (e.g. approving an already-denied RMA)
- **Money:** integer minor units + ISO 4217 code in every payload — never decimals/floats on the wire (ADR-0014); the minor-unit exponent comes from the currency registry (ADR-0046), never an assumed 2.
- **Enums cross HTTP as numbers** — never add `JsonStringEnumConverter` (AGENTS.md invariant).
- **Pagination:** `?page=&pageSize=` with a server-side clamp (public search: max 100, `X-Total-Count` header — `ProductsEndpoints`; admin lists clamp to 200). Many small admin lists are unpaginated today; paginate any list that can grow with tenant data.

## 4. Errors — ProblemDetails everywhere

- `AddApiProblemDetails()` + `UseApiProblemDetails()` (`BuildingBlocks.Infrastructure/Web/ProblemDetailsExtensions.cs`) in every service: **all** error responses are `application/problem+json` (RFC 9457, which obsoletes RFC 7807 — same media type, our PRD's "7807" references remain valid), and a malformed body (`BadHttpRequestException`) answers its own status (400) naming the parameter, never a 500.
- Validation failures return `ValidationProblemDetails` (400) with the `errors` dictionary.
- Never leak exception internals, SQL, or connection strings; include a `traceId` extension (auto via OpenTelemetry) so storefront errors correlate to traces.

## 5. Validation

- Use **.NET 10 built-in minimal API validation**: data annotations on request records, validated automatically before the handler runs; cross-field/async rules via endpoint filters or domain checks.

```csharp
public record AddItemRequest(
    [property: Required] Guid VariantId,
    [property: Range(1, 99)] int Quantity);
```

- Validate at the edge of the service (`builder.Services.AddValidation()` is registered in every service); Domain enforces invariants independently (defense in depth).

## 6. AuthN/AuthZ in handlers

- Services **never see the session cookie** — they validate the gateway-minted internal-claims JWT (handler from `BuildingBlocks.Infrastructure`) and read `sub`/`role` claims (ADR-0012).
- Authorization is layered: the role policy on the route group (`.RequireAuthorization(InternalClaimsAuth.AdminPolicy)`), the **PEP** for action permissions — `PermissionGate` (`BuildingBlocks.Infrastructure/Authz`) asks the Identity PDP and fails closed: `403` problem+json `permission_denied` plus a `Denied` audit entry (ADR-0025; e.g. refunds need `payment.refund`) — **plus** resource-ownership checks in the handler/domain (a customer reads only their own orders — return `404`, not `403`, to avoid existence leaks).
- **Tenant scope (ADR-0023/0024):** tenant-owned tables are under FORCE RLS, so every read/write runs inside `RunInTenantScopeAsync(TenantContext.ForTenant(id), …)` / `BeginTenantScopeAsync` with the tenant taken from the caller's claims — never from the request body. An unscoped write fails `42501` only as the non-superuser service role, so cover it with a non-superuser test.
- **Admin mutations emit audit** in the same unit of work (local hash-chained entry + `AuditEntryRecorded` through the outbox).
- Guest flows (cart, checkout, signed order links) accept anonymous requests but bind to the cart cookie / signed token explicitly.

## 7. Cross-cutting conventions

- **Idempotency:** every money-moving or saga-triggering POST is idempotent. Payments' money endpoints accept an `Idempotency-Key` header and replay the stored result (`IIdempotencyGuard` over `IdempotencyRecord` rows); checkout derives its payment key from the order id. There is no shared endpoint filter — follow the Payments pattern. Consumers dedup through the MassTransit inbox; webhooks by provider event id.
- **Outbox rule:** any handler that writes state **and** needs to notify others publishes through the MassTransit EF outbox in the same transaction — never `IPublishEndpoint` outside it (ADR-0007).
- **Rate limiting** is the gateway's job; services don't duplicate it (ADR-0011). Webhook endpoints (`/webhooks/{provider}` in Payments, via `InboundWebhookVerifier` and the per-provider secret registry) are the exception: signature verification *in the service*, always.
- **Health checks:** every service exposes `/health/live` and `/health/ready` (DB + broker checks) — excluded from OpenAPI and gateway public routes. Outside Production `MapServiceHealth` also maps `/health/error-probe` (a deliberate 500 for the error-inbox check, ADR-0067); keep any such diagnostic under `/health/` so the gateway's `/api/*/health*` block covers it.

## 8. OpenAPI & contract documentation

- Use first-party **`Microsoft.AspNetCore.OpenApi`** (default since .NET 9/10 — not Swashbuckle). `WithTags`, `WithSummary`, and `Produces` metadata on every endpoint; `Results<>` unions keep response docs honest.
- Per the repo Rules (AGENTS.md): when an endpoint is added/changed, export/update the service's contract file in `docs/api/` and update `docs/api/api_contracts_index.md`.
- **Message contracts** (`BuildingBlocks.Contracts`) version **additively only** — new optional fields, never renames/removals/type changes. Breaking change ⇒ new contract type (`OrderConfirmedV2`).

## 9. Testing expectations per endpoint

- Happy path + auth failure + validation failure, via `WebApplicationFactory` against Testcontainers Postgres (+ RabbitMQ harness where events are published).
- Saga-triggering endpoints additionally assert: idempotent replay (same `Idempotency-Key` ⇒ no duplicate effect) and outbox publication.

## Definition of done for an endpoint

- [ ] Lives in an `Endpoints` class with `MapGroup`, named static handler, `CancellationToken`
- [ ] Record DTOs in/out; no entity leakage; money as minor units
- [ ] `TypedResults` union; correct status codes; ProblemDetails on every failure path
- [ ] Built-in validation on the request record; ownership checks in handler/domain; tenant scope for tenant-owned rows; PEP permission for sensitive admin actions
- [ ] Idempotency + outbox rules honored if money/saga/events involved
- [ ] OpenAPI metadata present; `docs/api/` contract + index updated
- [ ] Tests: happy/auth/validation (+ idempotent replay for money paths)

---

## Sources

- [Minimal API endpoints in ASP.NET Core — complete guide for .NET 10](https://codewithmukesh.com/blog/minimal-apis-aspnet-core/)
- [Minimal APIs in the real world — filters, validation, versioning, rate limiting](https://www.dotnet-guide.com/tutorials/aspnet-core/minimal-apis-real-world/)
- [Minimal APIs: avoiding the Program.cs monster](https://medium.com/@anderson.buenogod/minimal-apis-in-asp-net-b364af5b027f)
- [Minimal API validation in .NET 10](https://dev.to/adrianbailador/minimal-api-validation-in-net-10-3c02)
- [Handle errors in ASP.NET Core APIs — Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling-api?view=aspnetcore-10.0)
- [Problem Details for ASP.NET Core APIs (RFC 9457)](https://www.milanjovanovic.tech/blog/problem-details-for-aspnetcore-apis)
- [ProblemDetails in ASP.NET Core — standardizing error responses](https://codewithmukesh.com/blog/problem-details-in-aspnet-core/)
- [RESTful API best practices for .NET developers](https://codewithmukesh.com/blog/restful-api-best-practices-for-dotnet-developers/)
