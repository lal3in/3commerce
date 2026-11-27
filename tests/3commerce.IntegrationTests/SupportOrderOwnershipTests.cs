using System.Net;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Support's order-scoped customer endpoints serve ONLY the user who placed the order (ADR-0018 amendment 2026-11-27):
/// Support's order copy records the owner (<c>OrderSnapshot.UserId</c>, from <c>OrderConfirmed.UserId</c>), and every
/// other caller — another customer, or anyone on a guest / not-yet-backfilled snapshot without an owner — gets 404, never
/// 403, so an order id confirms nothing. The refundable-lines read was anonymous before; it now needs a session (401).
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public sealed class SupportOrderOwnershipTests(Phase4Fixture fixture)
{
    private sealed record Created(Guid Id);
    private sealed record RmaCreated(Guid RmaId);
    private sealed record TicketSummary(Guid Id, Guid OrderId);

    private HttpClient As(string claims)
    {
        var c = fixture.Support.CreateClient();
        c.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, claims);
        return c;
    }

    private HttpClient Owner() => As(fixture.CustomerClaims());

    private HttpClient Stranger() => As(fixture.Claims("customer"));

    private static object Ticket(Guid orderId) =>
        new { orderId, email = "buyer@example.com", reason = 1, message = "Where is my order?" };

    private async Task<(Guid OrderId, Guid ProductId)> OwnedOrderAsync()
    {
        var orderId = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        await fixture.SeedOrderSnapshotAsync(orderId, 3000, "buyer@example.com", (product, "Widget", 1000, 3, 0));
        return (orderId, product);
    }

    [Fact]
    public async Task The_owner_reads_the_lines_opens_a_ticket_and_requests_a_refund()
    {
        var (orderId, product) = await OwnedOrderAsync();
        using var owner = Owner();

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/orders/{orderId}/lines")).StatusCode);

        var open = await owner.PostAsJsonAsync("/tickets", Ticket(orderId));
        Assert.Equal(HttpStatusCode.Created, open.StatusCode);
        var ticketId = (await open.Content.ReadFromJsonAsync<Created>())!.Id;
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/tickets/{ticketId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/tickets/{ticketId}/messages", new { body = "Any news?" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/tickets/by-order/{orderId}")).StatusCode);
        Assert.Contains(await owner.GetFromJsonAsync<List<TicketSummary>>("/tickets") ?? [], t => t.Id == ticketId);

        var rma = await owner.PostAsJsonAsync("/rma", new { orderId, reason = "damaged", lines = new[] { new { productId = product, quantity = 1 } } });
        Assert.Equal(HttpStatusCode.Accepted, rma.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/tickets/rmas/by-order/{orderId}")).StatusCode);
    }

    [Fact]
    public async Task Another_customer_gets_404_on_every_order_scoped_endpoint()
    {
        var (orderId, product) = await OwnedOrderAsync();
        using var owner = Owner();
        var ticketId = (await (await owner.PostAsJsonAsync("/tickets", Ticket(orderId))).Content.ReadFromJsonAsync<Created>())!.Id;
        using var stranger = Stranger();

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/orders/{orderId}/lines")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.PostAsJsonAsync("/rma", new { orderId, reason = "not mine", lines = new[] { new { productId = product, quantity = 1 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync("/tickets", Ticket(orderId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/tickets/by-order/{orderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/tickets/rmas/by-order/{orderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/tickets/{ticketId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/tickets/{ticketId}/messages", new { body = "hi" })).StatusCode);
        Assert.DoesNotContain(await stranger.GetFromJsonAsync<List<TicketSummary>>("/tickets") ?? [], t => t.OrderId == orderId);

        // Nothing was written on the stranger's behalf: the owner still sees all 3 units refundable and one ticket.
        Assert.Single(await owner.GetFromJsonAsync<List<TicketSummary>>($"/tickets/by-order/{orderId}") ?? []);
        Assert.Empty(await owner.GetFromJsonAsync<List<object>>($"/tickets/rmas/by-order/{orderId}") ?? []);
    }

    [Fact]
    public async Task An_ownerless_snapshot_fails_closed_even_for_a_signed_in_customer()
    {
        // A guest order, or one projected before OrderConfirmed carried UserId and not yet backfilled.
        var orderId = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        await fixture.SeedOwnedOrderSnapshotAsync(orderId, null, 1000, "guest@example.com", (product, "Widget", 1000, 1, 0));
        using var customer = Owner();

        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync($"/orders/{orderId}/lines")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.PostAsJsonAsync("/rma", new { orderId, reason = "guest" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.PostAsJsonAsync("/tickets", Ticket(orderId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync($"/tickets/by-order/{orderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync($"/tickets/rmas/by-order/{orderId}")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_order_is_the_same_404_and_anonymous_callers_get_401()
    {
        var (orderId, _) = await OwnedOrderAsync();
        using var owner = Owner();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/orders/{Guid.CreateVersion7()}/lines")).StatusCode);

        using var anon = fixture.Support.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/orders/{orderId}/lines")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/rma", new { orderId, reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/tickets/by-order/{orderId}")).StatusCode);
    }
}
