using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Payments.Api.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdmin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin").WithTags("Admin").RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/ledger/accounts", ListAccounts);
        group.MapGet("/ledger/entries", ListEntries);
        group.MapPost("/refunds", RequestRefund);
        return app;
    }

    private static async Task<Ok<List<AccountDto>>> ListAccounts(PaymentsDbContext db, CancellationToken ct)
    {
        var accounts = await db.LedgerAccounts.AsNoTracking()
            .Select(a => new AccountDto(a.Code, a.Name, a.Type.ToString()))
            .ToListAsync(ct);
        return TypedResults.Ok(accounts);
    }

    private static async Task<Ok<List<EntryDto>>> ListEntries(PaymentsDbContext db, string? reference, CancellationToken ct)
    {
        var query = db.JournalEntries.AsNoTracking().Include(e => e.Lines).AsQueryable();
        if (!string.IsNullOrEmpty(reference))
        {
            query = query.Where(e => e.Reference == reference);
        }

        var entries = await query.OrderByDescending(e => e.CreatedAt).Take(200)
            .Select(e => new EntryDto(
                e.Id, e.Description, e.Reference, e.Currency, e.CreatedAt,
                e.Lines.Select(l => new LineDto(l.AccountCode, l.DebitMinor, l.CreditMinor)).ToList()))
            .ToListAsync(ct);
        return TypedResults.Ok(entries);
    }

    /// <summary>
    /// Admin-initiated refund — publishes the single RefundRequested contract that the
    /// ExecuteRefundConsumer acts on (same path the Phase-4 RMA will use). Idempotency-Key required.
    /// </summary>
    private static async Task<Results<Accepted<RefundResponse>, BadRequest<string>>> RequestRefund(
        RefundRequest request, HttpContext http, IPublishEndpoint publisher, PaymentsDbContext db,
        IAuditRecorder audit, ClaimsPrincipal user, IConfiguration config, CancellationToken ct)
    {
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrEmpty(key))
        {
            return TypedResults.BadRequest("Idempotency-Key header is required.");
        }

        var requestHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{request.OrderId}:{request.AmountMinor}:{request.Reason}")));

        // Replay protection (NFR-3): same key returns the original response; same key with a
        // different body is a conflict.
        var existing = await db.IdempotencyRecords.FindAsync([key], ct);
        if (existing is not null)
        {
            return existing.RequestHash == requestHash
                ? TypedResults.Accepted((string?)null, System.Text.Json.JsonSerializer.Deserialize<RefundResponse>(existing.ResponseJson)!)
                : TypedResults.BadRequest("Idempotency-Key reused with a different request.");
        }

        var refundId = Guid.CreateVersion7();
        var response = new RefundResponse(refundId);
        db.IdempotencyRecords.Add(new ThreeCommerce.Payments.Domain.IdempotencyRecord
        {
            Key = key,
            RequestHash = requestHash,
            ResponseJson = System.Text.Json.JsonSerializer.Serialize(response),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await publisher.Publish(new RefundRequested(refundId, request.OrderId, request.AmountMinor, request.Reason, "admin"), ct);
        // Refund requests carry no tenant, so the entry lands under the configured default tenant —
        // the same default the Audit search (and Mission Control) falls back to.
        await audit.RecordAsync(user.Mutation(
            DefaultTenantId(config), "Refund", refundId.ToString(), "payments.refund.request", request.Reason), ct);
        // The bus outbox only delivers on SaveChanges; this also commits the idempotency record.
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted((string?)null, response);
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");
}

public record AccountDto(string Code, string Name, string Type);
public record LineDto(string AccountCode, long DebitMinor, long CreditMinor);
public record EntryDto(Guid Id, string Description, string Reference, string Currency, DateTimeOffset CreatedAt, List<LineDto> Lines);
public record RefundRequest([property: Required] Guid OrderId, [property: Range(1, long.MaxValue)] long AmountMinor, [property: Required] string Reason);
public record RefundResponse(Guid RefundId);
