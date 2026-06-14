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
        group.MapGet("/{id:guid}", GetTicket);
        group.MapPost("/{id:guid}/messages", AddMessage);

        // Request a refund/return for an order — starts the RMA saga.
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
        return TypedResults.Ok(tickets.Select(ToDto).ToList());
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

    private static async Task<Accepted<RmaCreatedDto>> RequestRma(
        RmaRequest request, SupportDbContext db, IPublishEndpoint publisher, CancellationToken ct)
    {
        var rmaId = Guid.CreateVersion7();
        await publisher.Publish(new RmaRequested(rmaId, request.OrderId, request.Email, request.AmountMinor, request.Reason), ct);
        await db.SaveChangesAsync(ct); // flush the bus outbox
        return TypedResults.Accepted((string?)null, new RmaCreatedDto(rmaId));
    }

    private static TicketDto ToDto(Ticket t) => new(
        t.Id, t.OrderId, t.Email, t.Reason.ToString(), t.Status.ToString(), t.CreatedAt,
        t.Messages.OrderBy(m => m.CreatedAt).Select(m => new MessageDto(m.Author.ToString(), m.Body, m.CreatedAt)).ToList());
}

public record OpenTicketRequest([property: Required] Guid OrderId, [property: Required, EmailAddress] string Email, [property: Required] TicketReason Reason, [property: Required] string Message);
public record MessageRequest([property: Required] string Body);
public record RmaRequest([property: Required] Guid OrderId, [property: Required, EmailAddress] string Email, [property: Range(1, long.MaxValue)] long AmountMinor, [property: Required] string Reason);
public record MessageDto(string Author, string Body, DateTimeOffset CreatedAt);
public record TicketDto(Guid Id, Guid OrderId, string Email, string Reason, string Status, DateTimeOffset CreatedAt, List<MessageDto> Messages);
public record RmaCreatedDto(Guid RmaId);
