using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Support.Domain;
using ThreeCommerce.Support.Infrastructure;

namespace ThreeCommerce.Support.Api.Endpoints;

/// <summary>
/// Operator ticket console: list every customer support ticket, read the thread, reply as the
/// operator, and open/close it. Admin-gated — the customer-facing <see cref="TicketEndpoints"/>
/// only ever posts as the customer and can't change status.
/// </summary>
public static class AdminTicketEndpoints
{
    public static IEndpointRouteBuilder MapAdminTickets(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/tickets").WithTags("Admin Tickets")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        group.MapGet("/", ListAll).WithSummary("List all support tickets (optionally filtered by status).");
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/{id:guid}/reply", Reply).WithSummary("Post an operator reply on a ticket.");
        group.MapPost("/{id:guid}/status", SetStatus).WithSummary("Open or close a ticket.");
        return app;
    }

    private static async Task<Ok<List<AdminTicketDto>>> ListAll(string? status, SupportDbContext db, CancellationToken ct)
    {
        var query = db.Tickets.AsNoTracking().Include(t => t.Messages).AsQueryable();
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<TicketStatus>(status, out var s))
        {
            query = query.Where(t => t.Status == s);
        }

        var tickets = await query.OrderByDescending(t => t.CreatedAt).Take(200).ToListAsync(ct);
        return TypedResults.Ok(tickets.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<AdminTicketDto>, NotFound>> Get(Guid id, SupportDbContext db, CancellationToken ct)
    {
        var t = await db.Tickets.AsNoTracking().Include(x => x.Messages).SingleOrDefaultAsync(x => x.Id == id, ct);
        return t is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(t));
    }

    private static async Task<Results<Ok<AdminTicketDto>, NotFound, BadRequest<string>>> Reply(
        Guid id, TicketReplyRequest request, SupportDbContext db, IAuditRecorder audit,
        ClaimsPrincipal user, IConfiguration config, TimeProvider time, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return TypedResults.BadRequest("A reply body is required.");
        }

        var ticket = await db.Tickets.Include(t => t.Messages).SingleOrDefaultAsync(t => t.Id == id, ct);
        if (ticket is null)
        {
            return TypedResults.NotFound();
        }

        var message = new TicketMessage
        {
            Id = Guid.CreateVersion7(),
            TicketId = ticket.Id,
            Author = MessageAuthor.Operator,
            Body = request.Body.Trim(),
            CreatedAt = time.GetUtcNow(),
        };
        ticket.Messages.Add(message);
        db.TicketMessages.Add(message);
        await audit.RecordAsync(user.Mutation(DefaultTenantId(config), "Ticket", id.ToString(), "support.ticket.reply"), ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(ticket));
    }

    private static async Task<Results<Ok<AdminTicketDto>, NotFound, BadRequest<string>>> SetStatus(
        Guid id, TicketStatusRequest request, SupportDbContext db, IAuditRecorder audit,
        ClaimsPrincipal user, IConfiguration config, CancellationToken ct)
    {
        if (!Enum.TryParse<TicketStatus>(request.Status, out var status))
        {
            return TypedResults.BadRequest("Unknown status.");
        }

        var ticket = await db.Tickets.Include(t => t.Messages).SingleOrDefaultAsync(t => t.Id == id, ct);
        if (ticket is null)
        {
            return TypedResults.NotFound();
        }

        ticket.Status = status;
        await audit.RecordAsync(user.Mutation(DefaultTenantId(config), "Ticket", id.ToString(), "support.ticket.status", status.ToString()), ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(ticket));
    }

    // The ticket carries no tenant, so audit entries land under the configured default tenant —
    // the same fallback AdminRmaEndpoints uses and that the Audit search (Mission Control) reads.
    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static AdminTicketDto ToDto(Ticket t) => new(
        t.Id, t.OrderId, t.Email, t.Reason.ToString(), t.Status.ToString(), t.CreatedAt,
        t.Messages.OrderBy(m => m.CreatedAt).Select(m => new AdminTicketMessageDto(m.Author.ToString(), m.Body, m.CreatedAt)).ToList());
}

public record TicketReplyRequest(string Body);
public record TicketStatusRequest(string Status);
public record AdminTicketMessageDto(string Author, string Body, DateTimeOffset CreatedAt);
public record AdminTicketDto(Guid Id, Guid OrderId, string Email, string Reason, string Status, DateTimeOffset CreatedAt, List<AdminTicketMessageDto> Messages);
