namespace ThreeCommerce.Support.Domain;

public enum TicketReason { WhereIsIt = 1, Damaged = 2, RefundRequest = 3, Other = 4 }
public enum TicketStatus { Open = 1, Closed = 2 }
public enum MessageAuthor { Customer = 1, Operator = 2 }

/// <summary>Order-linked support ticket (ADR-0018) — no chat/SLA/assignment in v1.</summary>
public class Ticket
{
    public Guid Id { get; init; }
    public Guid OrderId { get; init; }
    public required string Email { get; init; }
    public TicketReason Reason { get; init; }
    public TicketStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public List<TicketMessage> Messages { get; init; } = [];
}

public class TicketMessage
{
    public Guid Id { get; init; }
    public Guid TicketId { get; init; }
    public MessageAuthor Author { get; init; }
    public required string Body { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
