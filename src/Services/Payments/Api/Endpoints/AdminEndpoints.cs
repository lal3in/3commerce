using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Payments.Api.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdmin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin").WithTags("Admin").RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/ledger/accounts", ListAccounts);
        group.MapGet("/ledger/entries", ListEntries);
        group.MapGet("/ledger/balances", ListBalances);
        group.MapGet("/ledger/storefronts/{storefrontId:guid}/chart", StorefrontChart);
        group.MapPost("/refunds", RequestRefund);
        return app;
    }

    /// <summary>
    /// A storefront's full per-storefront chart of accounts (ledger_sf_4): every account role a movement
    /// for this store posts to, resolved to the store's own code — income codes from the projected
    /// <see cref="Ledger.StorefrontLedgerAccounts"/> (operator-configurable, ADR-0008) and the deterministic
    /// cost/settlement codes derived from the storefront id (Accounts.*StoreFor, one cash/fee/chargeback-fee
    /// triple per known PSP). This is the authoritative derivation; the admin overlays posted balances on it.
    /// No shared/default account appears — the whole point of the invariant.
    /// </summary>
    private static async Task<Ok<List<ChartAccountDto>>> StorefrontChart(Guid storefrontId, PaymentsDbContext db, CancellationToken ct)
    {
        var projection = await db.StorefrontLedgerAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.StorefrontId == storefrontId, ct);

        var sid = storefrontId;
        var rows = new List<ChartAccountDto>
        {
            new("Revenue", projection?.RevenueAccountCode ?? $"revenue.store-{sid:N}", "Sales revenue", nameof(AccountType.Revenue)),
            new("Refunds", Accounts.RefundsStoreFor(sid), "Refunds (contra-revenue)", nameof(AccountType.Revenue)),
            new("Shipping income", projection?.ShippingAccountCode ?? $"shipping.store-{sid:N}", "Shipping income", nameof(AccountType.Revenue)),
            new("Tax collected", projection?.TaxAccountCode ?? $"tax.store-{sid:N}", "Tax collected", nameof(AccountType.Liability)),
            new("Receivable", projection?.ReceivableAccountCode ?? $"receivable.store-{sid:N}", "PSP settlement receivable", nameof(AccountType.Asset)),
            new("COGS", Accounts.CogsStoreFor(sid), "Cost of goods sold", nameof(AccountType.Expense)),
            new("Carrier cost", Accounts.ShippingCostStoreFor(sid), "Carrier shipping cost", nameof(AccountType.Expense)),
            new("Write-offs", Accounts.WriteoffsStoreFor(sid), "Inventory write-offs", nameof(AccountType.Expense)),
            new("Supplier payable", Accounts.SupplierPayableStoreFor(sid), "Owed to suppliers", nameof(AccountType.Liability)),
            new("Carrier payable", Accounts.CarrierPayableStoreFor(sid), "Owed to carriers", nameof(AccountType.Liability)),
        };

        // One cash / processing-fee / chargeback-fee account per PSP the store can settle through.
        foreach (var provider in LedgerProviders.Known)
        {
            var name = char.ToUpperInvariant(provider[0]) + provider[1..];
            rows.Add(new ChartAccountDto($"Cash — {name}", Accounts.CashStoreFor(sid, provider), $"Cash settled via {name}", nameof(AccountType.Asset)));
            rows.Add(new ChartAccountDto($"{name} fees", Accounts.FeesStoreFor(sid, provider), $"{name} processing fees", nameof(AccountType.Expense)));
            rows.Add(new ChartAccountDto($"{name} chargeback fees", Accounts.ChargebackFeesStoreFor(sid, provider), $"{name} dispute fees", nameof(AccountType.Expense)));
        }

        return TypedResults.Ok(rows);
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
    /// Account balances grouped by (account, currency). A balance is only meaningful per currency —
    /// minor units are not comparable across currencies — so every row carries its currency and totals
    /// are never summed across them. The trial balance holds PER currency (Σ debits = Σ credits).
    /// </summary>
    private static async Task<Ok<List<BalanceDto>>> ListBalances(PaymentsDbContext db, CancellationToken ct)
    {
        var rows = await db.JournalLines.AsNoTracking()
            .GroupBy(l => new { l.AccountCode, l.Currency })
            .Select(g => new BalanceDto(
                g.Key.AccountCode,
                g.Key.Currency,
                g.Sum(x => x.DebitMinor),
                g.Sum(x => x.CreditMinor),
                g.Sum(x => x.DebitMinor) - g.Sum(x => x.CreditMinor)))
            .ToListAsync(ct);
        return TypedResults.Ok(rows
            .OrderBy(b => b.Currency, StringComparer.Ordinal)
            .ThenBy(b => b.AccountCode, StringComparer.Ordinal)
            .ToList());
    }

    /// <summary>
    /// Admin-initiated refund — publishes the single RefundRequested contract that the
    /// ExecuteRefundConsumer acts on (same path the Phase-4 RMA will use). Idempotency-Key required.
    /// </summary>
    private static async Task<Results<Accepted<RefundResponse>, BadRequest<string>>> RequestRefund(
        RefundRequest request, HttpContext http, IPublishEndpoint publisher,
        IIdempotencyGuard idempotency, IAuditRecorder audit, ClaimsPrincipal user, IConfiguration config, CancellationToken ct)
    {
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrEmpty(key))
        {
            return TypedResults.BadRequest("Idempotency-Key header is required.");
        }

        // Uniform replay protection via the shared guard (plan item 12): the same key returns the
        // stored response; the same key with a different body throws IdempotencyConflictException,
        // which the problem-details handler renders as a 409. The guard's SaveChanges also flushes
        // the outbox publish + audit entry recorded inside the operation, so all commit atomically.
        var response = await idempotency.ExecuteAsync(
            key,
            new { request.OrderId, request.AmountMinor, request.Reason },
            async token =>
            {
                var refundId = Guid.CreateVersion7();
                await publisher.Publish(new RefundRequested(refundId, request.OrderId, request.AmountMinor, request.Reason, "admin"), token);
                // Refund requests carry no tenant, so the entry lands under the configured default
                // tenant — the same default the Audit search (and Mission Control) falls back to.
                await audit.RecordAsync(user.Mutation(
                    DefaultTenantId(config), "Refund", refundId.ToString(), "payments.refund.request", request.Reason), token);
                return new RefundResponse(refundId);
            },
            ct);

        return TypedResults.Accepted((string?)null, response);
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");
}

public record AccountDto(string Code, string Name, string Type);
public record ChartAccountDto(string Role, string Code, string Name, string Type);
public record BalanceDto(string AccountCode, string Currency, long DebitMinor, long CreditMinor, long NetMinor);
public record LineDto(string AccountCode, long DebitMinor, long CreditMinor);
public record EntryDto(Guid Id, string Description, string Reference, string Currency, DateTimeOffset CreatedAt, List<LineDto> Lines);
public record RefundRequest([property: Required] Guid OrderId, [property: Range(1, long.MaxValue)] long AmountMinor, [property: Required] string Reason);
public record RefundResponse(Guid RefundId);
