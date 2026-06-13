# API Endpoint Guidelines

Standards for building HTTP endpoints in the six **ASP.NET Core minimal API** services behind the YARP gateway. Grounded in ADR-0006/0007/0011/0012 and the PRD API conventions (§10). Read this before adding or changing any endpoint.

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
- Admin endpoints live under `/admin/...` within each service and require the `admin` role.
- Route params bind to strongly-typed values (`Guid`, not `string`).

## 3. Requests, responses, status codes

- **DTOs are records**, defined in the service's `Api/` layer. Never expose EF entities or domain objects directly; map explicitly.
- Use **`TypedResults`** with `Results<T1, T2, ...>` unions so response types are compile-checked and flow into OpenAPI automatically.
- Status codes:
  - `200` reads · `201 + Location` creations · `204` deletes/no-content updates
  - `202`-semantics for saga starts: checkout returns `201` with the order in `PaymentRequested` state — **never block an HTTP request on saga completion** (ADR-0007)
  - `400` malformed · `422` validation failures · `401` unauthenticated · `403` unauthorized · `404` not-found-or-not-yours · `409` state conflicts (e.g. approving an already-denied RMA)
- **Money:** integer minor units + ISO 4217 code in every payload — never decimals/floats on the wire (ADR-0014).
- **Pagination:** `?page=&pageSize=` (max 100) + `X-Total-Count` header on all list endpoints.

## 4. Errors — ProblemDetails everywhere

- `AddProblemDetails()` + the global exception handler in every service: **all** error responses are `application/problem+json` (RFC 9457, which obsoletes RFC 7807 — same media type, our PRD's "7807" references remain valid).
- Validation failures return `ValidationProblemDetails` (422) with the `errors` dictionary.
- Never leak exception internals, SQL, or connection strings; include a `traceId` extension (auto via OpenTelemetry) so storefront errors correlate to traces.

## 5. Validation

- Use **.NET 10 built-in minimal API validation**: data annotations on request records, validated automatically before the handler runs; cross-field/async rules via endpoint filters or domain checks.

```csharp
public record AddItemRequest(
    [property: Required] Guid VariantId,
    [property: Range(1, 99)] int Quantity);
```

- Validate at the edge of the service; Domain enforces invariants independently (defense in depth). 422 for both layers.

## 6. AuthN/AuthZ in handlers

- Services **never see the session cookie** — they validate the gateway-minted internal-claims JWT (handler from `BuildingBlocks.Infrastructure`) and read `sub`/`role` claims (ADR-0012).
- Authorization is two-layer: role policy on the route group (`.RequireAuthorization("Admin")`) **plus** resource-ownership checks in the handler/domain (a customer reads only their own orders — return `404`, not `403`, to avoid existence leaks).
- Guest flows (cart, checkout, signed order links) accept anonymous requests but bind to the cart cookie / signed token explicitly.

## 7. Cross-cutting conventions

- **Idempotency:** every money-moving or saga-triggering POST accepts an `Idempotency-Key` header (replays return the original result); consumers/webhooks dedup by message/event ID — both via shared endpoint filter / consumer middleware in `BuildingBlocks`.
- **Outbox rule:** any handler that writes state **and** needs to notify others publishes through the MassTransit EF outbox in the same transaction — never `IPublishEndpoint` outside it (ADR-0007).
- **Rate limiting** is the gateway's job; services don't duplicate it (ADR-0011). Webhook endpoints (`/webhooks/stripe`) are the exception: signature verification *in the service*, always.
- **Health checks:** every service exposes `/health/live` and `/health/ready` (DB + broker checks) — excluded from OpenAPI and gateway public routes.

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
- [ ] Built-in validation on the request record; ownership checks in handler/domain
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
