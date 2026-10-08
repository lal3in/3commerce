namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Tests;

public class BackfillOptionsAndQueuesTests
{
    [Fact]
    public void Parses_a_full_command_line()
    {
        var tenant = Guid.NewGuid();
        var o = BackfillOptions.Parse(
            ["--target", "both", "--execute", "--tenant", tenant.ToString(), "--limit", "5", "--list",
             "--email-window", "4", "--email-skew", "0", "--email-orphan-reach", "60", "--include-unproven-email", "--skip-preflight"]);

        Assert.Equal([BackfillTarget.Fulfillment, BackfillTarget.Notifications], o.Targets);
        Assert.True(o.Execute);
        Assert.Equal([tenant], o.Tenants);
        Assert.Equal(5, o.Limit);
        Assert.True(o.List && o.IncludeUnprovenEmail && o.SkipPreflight);
        Assert.Equal(new EmailMatchOptions(TimeSpan.FromSeconds(4), TimeSpan.Zero, TimeSpan.FromMinutes(60)), o.EmailMatch);
    }

    [Fact]
    public void Defaults_are_a_safe_dry_run_matching_with_the_default_window()
    {
        var o = BackfillOptions.Parse(["--target", "fulfillment", "--dry-run"]);

        Assert.False(o.Execute);
        Assert.Empty(o.Tenants);
        Assert.Null(o.Limit);
        Assert.False(o.IncludeUnprovenEmail);
        Assert.Equal(EmailMatchOptions.Default, o.EmailMatch);
    }

    [Theory]
    [InlineData("--target", "both")]                                   // neither --dry-run nor --execute: never send by default
    [InlineData("--dry-run")]                                          // no target
    [InlineData("--target", "both", "--dry-run", "--execute")]          // contradictory
    [InlineData("--target", "everything", "--dry-run")]
    [InlineData("--target", "both", "--dry-run", "--tenant", "acme")]
    [InlineData("--target", "both", "--dry-run", "--limit", "0")]
    [InlineData("--target", "both", "--dry-run", "--limit")]
    [InlineData("--target", "both", "--dry-run", "--bogus")]
    public void Rejects_unsafe_or_malformed_command_lines(params string[] args) =>
        Assert.Throws<BackfillUsageException>(() => BackfillOptions.Parse(args));

    [Fact]
    public void Queue_names_are_the_ADR_0060_single_owner_queues()
    {
        Assert.Equal("fulfillment-order-confirmed", BackfillQueues.QueueName(BackfillTarget.Fulfillment));
        Assert.Equal("order-confirmed", BackfillQueues.QueueName(BackfillTarget.Notifications));
        Assert.Equal(new Uri("queue:fulfillment-order-confirmed"), BackfillQueues.Address(BackfillTarget.Fulfillment));
    }

    [Fact]
    public void Message_ids_are_deterministic_per_target_and_order()
    {
        var order = Guid.CreateVersion7();

        Assert.Equal(BackfillQueues.MessageId(BackfillTarget.Fulfillment, order), BackfillQueues.MessageId(BackfillTarget.Fulfillment, order));
        Assert.NotEqual(BackfillQueues.MessageId(BackfillTarget.Fulfillment, order), BackfillQueues.MessageId(BackfillTarget.Notifications, order));
        Assert.NotEqual(BackfillQueues.MessageId(BackfillTarget.Fulfillment, order), BackfillQueues.MessageId(BackfillTarget.Fulfillment, Guid.CreateVersion7()));
    }
}
