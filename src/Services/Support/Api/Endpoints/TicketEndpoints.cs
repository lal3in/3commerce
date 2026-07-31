using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Support;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Support.Domain;
using ThreeCommerce.Support.Infrastructure;
using ThreeCommerce.Support.Infrastructure.Sagas;

namespace ThreeCommerce.Support.Api.Endpoints;

public static class TicketEndpoints
{
    public static IEndpointRouteBuilder MapTickets(this IEndpointRouteBuilder app)
    {
        // Authenticated customers (incl. guests who set a password post-purchase). Guest
        // signed-link access is a documented v1 simplification.
        var group = app.MapGroup("/tickets").WithTags("Support").RequireAuthorization(InternalClaimsAuth.CustomerPolicy);
        group.MapPost("/", OpenTicket);
        group.MapGet("/", ListTickets);
        group.MapGet("/by-order/{orderId:guid}", ListByOrder);
        // The customer's own refund/return requests for one order, with live lifecycle status.
        group.MapGet("/rmas/by-order/{orderId:guid}", ListRmasByOrder);
        group.MapGet("/{id:guid}", GetTicket);
        group.MapPost("/{id:guid}/messages", AddMessage);

        // Refundable lines for an order (by id, like order-status polling) — drives the RMA UI.
        app.MapGet("/orders/{orderId:guid}/lines", GetRefundableLines).WithTags("Support");
        // Request a refund/return — customer selects lines; the amount is computed server-side (BL-8).
        app.MapPost("/rma", RequestRma).WithTags("Support").RequireAuthorization(InternalClaimsAuth.CustomerPolicy);
        return app;
    }

    private static string Email(ClaimsPrincipal user) => user.FindFirstValue("sub")!; // sub is the user id; email not in claims

    private static async Task<Created<TicketDto>> OpenTicket(
        OpenTicketRequest request, ClaimsPrincipal user, SupportDbContext db, IPublishEndpoint publisher, TimeProvider time, CancellationToken ct)
    {
        var ticket = new Ticket
        {
            Id = Guid.CreateVersion7(),
            OrderId = request.OrderId,
            Email = request.Email,
            Reason = request.Reason,
            Status = TicketStatus.Open,
            CreatedAt = time.GetUtcNow(),
            Messages =
            [
                new TicketMessage { Id = Guid.CreateVersion7(), Author = MessageAuthor.Customer, Body = request.Message, CreatedAt = time.GetUtcNow() },
            ],
        };
        db.Tickets.Add(ticket);
        await publisher.Publish(new TicketOpened(ticket.Id, ticket.OrderId, ticket.Email, ticket.Reason.ToString()), ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/tickets/{ticket.Id}", ToDto(ticket));
    }

    private static async Task<Ok<List<TicketDto>>> ListTickets(SupportDbContext db, CancellationToken ct)
    {
        var tickets = await db.Tickets.AsNoTracking().Include(t => t.Messages)
            .OrderByDescending(t => t.CreatedAt).Take(100).ToListAsync(ct);
        return TypedResults.Ok(tickets.Select(t => ToDto(t)).ToList());
    }

    // Ticket history for one order (drives the customer's "your requests" thread list on the order
    // support page). Order-scoped: the support page is already the owner's per-order page.
    private static async Task<Ok<List<TicketDto>>> ListByOrder(Guid orderId, SupportDbContext db, CancellationToken ct)
    {
        var tickets = await db.Tickets.AsNoTracking().Include(t => t.Messages)
            .Where(t => t.OrderId == orderId).OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
        var attachments = await AttachmentsForTicketsAsync(db, tickets.Select(t => t.Id).ToList(), ct);
        return TypedResults.Ok(tickets.Select(t => ToDto(t, attachments)).ToList());
    }

    // Attachments for a set of tickets, grouped by ticket id (OwnerKind = "ticket").
    internal static async Task<ILookup<Guid, AttachmentDto>> AttachmentsForTicketsAsync(SupportDbContext db, List<Guid> ticketIds, CancellationToken ct)
    {
        if (ticketIds.Count == 0)
        {
            return Array.Empty<(Guid, AttachmentDto)>().ToLookup(x => x.Item1, x => x.Item2);
        }

        var rows = await db.Attachments.AsNoTracking()
            .Where(a => a.OwnerKind == "ticket" && ticketIds.Contains(a.OwnerId))
            .OrderBy(a => a.CreatedAt)
            .Select(a => new { a.OwnerId, Dto = new AttachmentDto(a.Id, a.FileName, a.ContentType, a.SizeBytes) })
            .ToListAsync(ct);
        return rows.ToLookup(r => r.OwnerId, r => r.Dto);
    }

    private static async Task<Results<Ok<TicketDto>, NotFound>> GetTicket(Guid id, SupportDbContext db, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Messages).SingleOrDefaultAsync(t => t.Id == id, ct);
        return ticket is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(ticket));
    }

    private static async Task<Results<Ok<TicketDto>, NotFound>> AddMessage(
        Guid id, MessageRequest request, SupportDbContext db, TimeProvider time, CancellationToken ct)
    {
        var ticket = await db.Tickets.Include(t => t.Messages).SingleOrDefaultAsync(t => t.Id == id, ct);
        if (ticket is null)
        {
            return TypedResults.NotFound();
        }

        var message = new TicketMessage { Id = Guid.CreateVersion7(), TicketId = ticket.Id, Author = MessageAuthor.Customer, Body = request.Body, CreatedAt = time.GetUtcNow() };
        ticket.Messages.Add(message);
        db.TicketMessages.Add(message);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(ticket));
    }

    private static async Task<Results<Accepted<RmaCreatedDto>, NotFound, BadRequest<string>>> RequestRma(
        RmaRequest request, SupportDbContext db, IPublishEndpoint publisher, TimeProvider time, CancellationToken ct)
    {
        var snapshot = await db.OrderSnapshots.Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.OrderId == request.OrderId, ct);
        if (snapshot is null)
        {
            return TypedResults.NotFound();
        }

        // Units already spoken for by earlier (non-denied) requests — so a second request can only
        // claim what's still refundable, and the recorded lines stay honest.
        var consumed = await ConsumedQuantitiesAsync(db, request.OrderId, ct);
        int Remaining(OrderSnapshotLine l) => Math.Max(0, l.Quantity - consumed.GetValueOrDefault(l.ProductId));

        var rmaId = Guid.CreateVersion7();
        var recordLines = new List<RmaRequestLine>();

        // Server-derived amount (BL-8): empty selection => whatever is left of the whole order; else
        // the chosen lines, each capped at the still-refundable quantity. The client never sends an amount.
        long amount = 0;
        if (request.Lines is null || request.Lines.Count == 0)
        {
            foreach (var line in snapshot.Lines)
            {
                var qty = Remaining(line);
                if (qty <= 0) continue;
                amount += line.UnitPriceMinor * qty;
                recordLines.Add(NewLine(rmaId, line, qty));
            }
        }
        else
        {
            foreach (var sel in request.Lines)
            {
                var line = snapshot.Lines.FirstOrDefault(l => l.ProductId == sel.ProductId);
                if (line is null)
                {
                    return TypedResults.BadRequest("Unknown line in selection.");
                }

                var qty = Math.Clamp(sel.Quantity, 0, Remaining(line));
                if (qty <= 0) continue;
                amount += line.UnitPriceMinor * qty;
                recordLines.Add(NewLine(rmaId, line, qty));
            }
        }

        if (amount <= 0)
        {
            return TypedResults.BadRequest("Nothing left to refund on this order.");
        }

        db.RmaRequests.Add(new RmaRequestRecord
        {
            Id = rmaId,
            OrderId = request.OrderId,
            Email = snapshot.Email,
            Reason = request.Reason,
            AmountMinor = amount,
            Currency = snapshot.Currency,
            CreatedAt = time.GetUtcNow(),
            Lines = recordLines,
        });
        await publisher.Publish(new RmaRequested(rmaId, request.OrderId, snapshot.Email, amount, request.Reason), ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted((string?)null, new RmaCreatedDto(rmaId));

        static RmaRequestLine NewLine(Guid rmaId, OrderSnapshotLine l, int qty) => new()
        {
            Id = Guid.CreateVersion7(), RmaId = rmaId, ProductId = l.ProductId,
            Title = l.Title, Quantity = qty, UnitPriceMinor = l.UnitPriceMinor,
        };
    }

    // Units already claimed per product on an order by requests that haven't been denied — used both to
    // decrement the refundable lines shown to the customer and to cap a new request server-side.
    internal static async Task<Dictionary<Guid, int>> ConsumedQuantitiesAsync(SupportDbContext db, Guid orderId, CancellationToken ct)
    {
        var records = await db.RmaRequests.AsNoTracking().Include(r => r.Lines)
            .Where(r => r.OrderId == orderId).ToListAsync(ct);
        if (records.Count == 0)
        {
            return [];
        }

        var deniedIds = (await db.Rmas.AsNoTracking()
            .Where(s => s.OrderId == orderId && s.CurrentState == "Denied")
            .Select(s => s.CorrelationId).ToListAsync(ct)).ToHashSet();

        return records.Where(r => !deniedIds.Contains(r.Id))
            .SelectMany(r => r.Lines)
            .GroupBy(l => l.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
    }

    private static async Task<Results<Ok<RefundableOrderDto>, NotFound>> GetRefundableLines(
        Guid orderId, SupportDbContext db, CancellationToken ct)
    {
        var snap = await db.OrderSnapshots.AsNoTracking().Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.OrderId == orderId, ct);
        if (snap is null)
        {
            return TypedResults.NotFound();
        }

        // Show what's STILL refundable: purchased quantity minus units already requested (excl. denied).
        var consumed = await ConsumedQuantitiesAsync(db, orderId, ct);
        return TypedResults.Ok(new RefundableOrderDto(
            snap.OrderId, snap.GrossMinor, snap.Currency,
            snap.Lines
                .Select(l => new RefundableLineDto(l.ProductId, l.Title, l.UnitPriceMinor, Math.Max(0, l.Quantity - consumed.GetValueOrDefault(l.ProductId))))
                .ToList()));
    }

    // The customer's refund/return requests for one order, each enriched with its live saga state
    // (defaults to "Requested" until the RMA saga has materialized its read-model row).
    private static async Task<Ok<List<CustomerRmaDto>>> ListRmasByOrder(Guid orderId, SupportDbContext db, CancellationToken ct)
    {
        var records = await db.RmaRequests.AsNoTracking().Include(r => r.Lines)
            .Where(r => r.OrderId == orderId).OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        var states = await db.Rmas.AsNoTracking().Where(s => s.OrderId == orderId)
            .ToDictionaryAsync(s => s.CorrelationId, s => s.CurrentState, ct);
        return TypedResults.Ok(records.Select(r => new CustomerRmaDto(
            r.Id, r.AmountMinor, r.Currency, r.Reason,
            states.GetValueOrDefault(r.Id, "Requested"), r.CreatedAt,
            r.Lines.Select(l => new CustomerRmaLineDto(l.ProductId, l.Title, l.Quantity, l.UnitPriceMinor)).ToList())).ToList());
    }

    private static TicketDto ToDto(Ticket t, ILookup<Guid, AttachmentDto>? attachments = null) => new(
        t.Id, t.OrderId, t.Email, t.Reason.ToString(), t.Status.ToString(), t.CreatedAt,
        t.Messages.OrderBy(m => m.CreatedAt).Select(m => new MessageDto(m.Author.ToString(), m.Body, m.CreatedAt)).ToList(),
        attachments?[t.Id].ToList() ?? []);
}

public record OpenTicketRequest([property: Required] Guid OrderId, [property: Required, EmailAddress] string Email, [property: Required] TicketReason Reason, [property: Required] string Message);
public record MessageRequest([property: Required] string Body);
public record RmaLineSelection([property: Required] Guid ProductId, [property: Range(1, 999)] int Quantity);
public record RmaRequest([property: Required] Guid OrderId, [property: Required] string Reason, List<RmaLineSelection>? Lines);
public record RefundableLineDto(Guid ProductId, string Title, long UnitPriceMinor, int Quantity);
public record RefundableOrderDto(Guid OrderId, long GrossMinor, string Currency, List<RefundableLineDto> Lines);
public record CustomerRmaLineDto(Guid ProductId, string Title, int Quantity, long UnitPriceMinor);
public record CustomerRmaDto(Guid Id, long AmountMinor, string Currency, string Reason, string State, DateTimeOffset CreatedAt, List<CustomerRmaLineDto> Lines);
public record MessageDto(string Author, string Body, DateTimeOffset CreatedAt);
public record TicketDto(Guid Id, Guid OrderId, string Email, string Reason, string Status, DateTimeOffset CreatedAt, List<MessageDto> Messages, List<AttachmentDto> Attachments);
public record RmaCreatedDto(Guid RmaId);
