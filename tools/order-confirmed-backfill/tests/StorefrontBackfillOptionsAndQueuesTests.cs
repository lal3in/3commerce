using ThreeCommerce.Tools.OrderConfirmedBackfill.Storefronts;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Tests;

public class StorefrontBackfillOptionsAndQueuesTests
{
    [Fact]
    public void Parses_a_full_command_line()
    {
        var tenant = Guid.NewGuid();
        var o = StorefrontBackfillOptions.Parse(
            ["--target", "both", "--execute", "--tenant", tenant.ToString(), "--limit", "2", "--list", "--clone-window", "60", "--skip-preflight"]);

        Assert.Equal([StorefrontBackfillTarget.Payments, StorefrontBackfillTarget.Fulfillment], o.Targets);
        Assert.True(o.Execute);
        Assert.Equal([tenant], o.Tenants);
        Assert.Equal(2, o.Limit);
        Assert.True(o.List && o.SkipPreflight);
        Assert.Equal(TimeSpan.FromSeconds(60), o.CloneWindow);
    }

    [Fact]
    public void Defaults_are_a_safe_dry_run()
    {
        var o = StorefrontBackfillOptions.Parse(["--target", "payments", "--dry-run"]);

        Assert.False(o.Execute);
        Assert.Empty(o.Tenants);
        Assert.Null(o.Limit);
        Assert.False(o.SkipPreflight);
        Assert.Equal(StorefrontBackfillPlanner.DefaultCloneWindow, o.CloneWindow);
    }

    [Theory]
    [InlineData("--target", "both")]                                   // neither --dry-run nor --execute
    [InlineData("--dry-run")]                                          // no target
    [InlineData("--target", "both", "--dry-run", "--execute")]
    [InlineData("--target", "notifications", "--dry-run")]             // an order-backfill target is not a storefront one
    [InlineData("--target", "both", "--dry-run", "--tenant", "acme")]
    [InlineData("--target", "both", "--dry-run", "--limit", "0")]
    [InlineData("--target", "both", "--dry-run", "--clone-window", "-5")]
    [InlineData("--target", "both", "--dry-run", "--email-window", "1")] // order-backfill options are not accepted
    public void Rejects_unsafe_or_malformed_command_lines(params string[] args) =>
        Assert.Throws<BackfillUsageException>(() => StorefrontBackfillOptions.Parse(args));

    [Fact]
    public void Queue_names_are_the_ADR_0060_single_owner_queues()
    {
        Assert.Equal("storefront-duplicated", StorefrontBackfillQueues.QueueName(StorefrontBackfillTarget.Payments));
        Assert.Equal("fulfillment-storefront-duplicated", StorefrontBackfillQueues.QueueName(StorefrontBackfillTarget.Fulfillment));
        Assert.Equal(new Uri("queue:fulfillment-storefront-duplicated"), StorefrontBackfillQueues.Address(StorefrontBackfillTarget.Fulfillment));
        Assert.Equal("3commerce.Payments.Api", StorefrontBackfillQueues.ExpectedConnectionName(StorefrontBackfillTarget.Payments));
    }

    [Fact]
    public void Message_ids_are_deterministic_per_target_and_duplicate_and_distinct_from_order_backfill_ids()
    {
        var store = Guid.CreateVersion7();

        Assert.Equal(StorefrontBackfillQueues.MessageId(StorefrontBackfillTarget.Payments, store), StorefrontBackfillQueues.MessageId(StorefrontBackfillTarget.Payments, store));
        Assert.NotEqual(StorefrontBackfillQueues.MessageId(StorefrontBackfillTarget.Payments, store), StorefrontBackfillQueues.MessageId(StorefrontBackfillTarget.Fulfillment, store));
        Assert.NotEqual(StorefrontBackfillQueues.MessageId(StorefrontBackfillTarget.Fulfillment, store), BackfillQueues.MessageId(BackfillTarget.Fulfillment, store));
    }
}
