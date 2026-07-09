using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Contracts.Identity;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// FR-7 in both directions: when an email is verified, prior guest orders placed with that
/// email attach to the account (GuestOrderAttachConsumer); and a guest order that only
/// materializes AFTER verification (payment settled later than the conversion) attaches at
/// creation time (OrderStatusConsumer + VerifiedCustomerCopy). Orders with other emails are
/// untouched, and attachment always requires a VERIFIED email.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class GuestConversionTests(Phase3Fixture fixture)
{
    private sealed record CheckoutResponseDto(Guid OrderId);
    private sealed record OrderSummaryDto(Guid Id, string Status, long GrossMinor, string Currency, DateTimeOffset CreatedAt);

    [Fact]
    public async Task EmailVerified_attaches_matching_guest_orders()
    {
        var email = $"guest-{Guid.NewGuid():N}@example.com";
        var otherEmail = $"other-{Guid.NewGuid():N}@example.com";
        var userId = Guid.CreateVersion7();

        var mine = await fixture.SeedGuestOrderAsync(email);
        var notMine = await fixture.SeedGuestOrderAsync(otherEmail);

        // Identity would publish this on verification.
        await fixture.PublishAsync(new EmailVerified(userId, email));

        // The matching order attaches; the other stays a guest order.
        await WaitUntilAsync(async () => await fixture.OrderUserIdAsync(mine) is not null);

        Assert.Equal(userId, await fixture.OrderUserIdAsync(mine));
        Assert.Null(await fixture.OrderUserIdAsync(notMine));
    }

    [Fact]
    public async Task EmailVerified_is_case_insensitive_on_email()
    {
        var email = $"Mixed-{Guid.NewGuid():N}@Example.com";
        var orderId = await fixture.SeedGuestOrderAsync(email);
        var userId = Guid.CreateVersion7();

        await fixture.PublishAsync(new EmailVerified(userId, email.ToLowerInvariant()));

        await WaitUntilAsync(async () => await fixture.OrderUserIdAsync(orderId) is not null);

        Assert.Equal(userId, await fixture.OrderUserIdAsync(orderId));
    }

    /// <summary>
    /// The mr_1 regression: the shopper converts to an account and verifies BEFORE the order
    /// row exists (the Order is only materialized when the saga confirms payment). The
    /// EmailVerified sweep finds nothing at that moment — the attach must instead happen when
    /// the order is created, and the order must then show up in the account's order list.
    /// </summary>
    [Fact]
    public async Task Order_confirmed_after_verification_attaches_at_creation_and_lists_in_order_history()
    {
        var email = $"early-verifier-{Guid.NewGuid():N}@example.com";
        var userId = Guid.CreateVersion7();

        // 1. Verification happens first; Ordering records the verified account copy.
        await fixture.PublishAsync(new EmailVerified(userId, email));
        await WaitUntilAsync(async () => await fixture.VerifiedCustomerUserIdAsync(email) is not null);

        // 2. Guest checkout (no session): the order row does not exist yet.
        var productId = await fixture.SeedProductAsync(2_500);
        using var shopper = fixture.Ordering.CreateClient();
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var checkout = await shopper.PostAsJsonAsync("/checkout", new
        {
            // Mixed case on purpose: attachment matches case-insensitively.
            email = email.ToUpperInvariant(),
            shippingAddress = new { name = "G", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
        });
        checkout.EnsureSuccessStatusCode();
        var orderId = (await checkout.Content.ReadFromJsonAsync<CheckoutResponseDto>())!.OrderId;

        // 3. The saga confirms payment (stand-in publish) — the order materializes now and
        //    must attach to the already-verified account at creation.
        await fixture.PublishAsync(new CheckoutCompleted(orderId));
        await WaitUntilAsync(() => fixture.OrderExistsAsync(orderId));
        Assert.Equal(userId, await fixture.OrderUserIdAsync(orderId));

        // 4. The order appears in the user's order list (the account page query).
        using var account = fixture.Ordering.CreateClient();
        account.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(userId, "customer"));
        var orders = await account.GetFromJsonAsync<List<OrderSummaryDto>>("/orders");
        Assert.Contains(orders!, o => o.Id == orderId);
    }

    /// <summary>Guest orders for an email nobody verified stay unattached (the security invariant).</summary>
    [Fact]
    public async Task Order_confirmed_for_an_unverified_email_stays_a_guest_order()
    {
        var email = $"never-verified-{Guid.NewGuid():N}@example.com";

        var productId = await fixture.SeedProductAsync(2_500);
        using var shopper = fixture.Ordering.CreateClient();
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var checkout = await shopper.PostAsJsonAsync("/checkout", new
        {
            email,
            shippingAddress = new { name = "G", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
        });
        checkout.EnsureSuccessStatusCode();
        var orderId = (await checkout.Content.ReadFromJsonAsync<CheckoutResponseDto>())!.OrderId;

        await fixture.PublishAsync(new CheckoutCompleted(orderId));
        await WaitUntilAsync(() => fixture.OrderExistsAsync(orderId));

        Assert.Null(await fixture.OrderUserIdAsync(orderId));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (!await condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
        }
    }
}
