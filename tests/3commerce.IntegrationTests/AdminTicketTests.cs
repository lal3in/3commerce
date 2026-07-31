using System.Net;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Operator ticket console (AdminTicketEndpoints): an admin lists every ticket, replies as the
/// operator (not the customer), and opens/closes it — and a customer can't reach the admin routes.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class AdminTicketTests(Phase4Fixture fixture)
{
    private sealed record MessageDto(string Author, string Body, DateTimeOffset CreatedAt);
    private sealed record TicketDto(Guid Id, Guid OrderId, string Email, string Reason, string Status, DateTimeOffset CreatedAt, List<MessageDto> Messages);

    private HttpClient Customer()
    {
        var c = fixture.Support.CreateClient();
        c.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.Claims("customer"));
        return c;
    }

    private HttpClient Admin()
    {
        var c = fixture.Support.CreateClient();
        c.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.Claims("admin"));
        return c;
    }

    private static async Task<Guid> OpenTicketAsync(HttpClient customer)
    {
        var open = await customer.PostAsJsonAsync("/tickets", new
        {
            orderId = Guid.CreateVersion7(),
            email = $"shopper-{Guid.NewGuid():N}@example.test",
            reason = 1, // WhereIsIt
            message = "Where is my order?",
        });
        open.EnsureSuccessStatusCode();
        return (await open.Content.ReadFromJsonAsync<TicketDto>())!.Id;
    }

    [Fact]
    public async Task Admin_lists_replies_as_operator_and_closes_a_ticket()
    {
        var id = await OpenTicketAsync(Customer());
        var admin = Admin();

        // Lists all tickets — the new one is there.
        var list = await admin.GetFromJsonAsync<List<TicketDto>>("/admin/tickets");
        Assert.Contains(list!, t => t.Id == id);

        // Operator reply lands as an Operator-authored message on the thread.
        var reply = await admin.PostAsJsonAsync($"/admin/tickets/{id}/reply", new { body = "On its way!" });
        reply.EnsureSuccessStatusCode();
        var afterReply = await reply.Content.ReadFromJsonAsync<TicketDto>();
        Assert.Contains(afterReply!.Messages, m => m.Author == "Operator" && m.Body == "On its way!");

        // Close it — status flips to Closed.
        var close = await admin.PostAsJsonAsync($"/admin/tickets/{id}/status", new { status = "Closed" });
        close.EnsureSuccessStatusCode();
        Assert.Equal("Closed", (await close.Content.ReadFromJsonAsync<TicketDto>())!.Status);

        // Status filter narrows to closed tickets.
        var closed = await admin.GetFromJsonAsync<List<TicketDto>>("/admin/tickets?status=Closed");
        Assert.Contains(closed!, t => t.Id == id);
    }

    [Fact]
    public async Task Customer_cannot_reach_the_operator_console()
    {
        var response = await Customer().GetAsync("/admin/tickets");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
