using ThreeCommerce.BuildingBlocks.Contracts.Identity;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// FR-7: when an email is verified, prior guest orders placed with that email attach to the
/// account (Ordering's GuestOrderAttachConsumer). Orders with other emails are untouched.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class GuestConversionTests(Phase3Fixture fixture)
{
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
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (await fixture.OrderUserIdAsync(mine) is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
        }

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

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (await fixture.OrderUserIdAsync(orderId) is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
        }

        Assert.Equal(userId, await fixture.OrderUserIdAsync(orderId));
    }
}
