using ThreeCommerce.BuildingBlocks.Infrastructure.Notifications;

namespace ThreeCommerce.Entity.Tests;

public class NotificationsTests
{
    [Fact]
    public void Security_alerts_are_always_delivered_even_when_everything_is_muted()
    {
        var muted = new NotificationPreferences(MarketingOptIn: false, TransactionalMuted: true);
        var alert = SecurityAlert.ForAuditEvent("ops@tenant.com", "supplier.change_request.approve", "SupplierChangeRequest", "r1", "Denied");

        Assert.Equal(NotificationCategory.Security, alert.Category);
        Assert.True(NotificationPolicy.ShouldDeliver(alert, muted));
    }

    [Fact]
    public void Marketing_requires_opt_in_transactional_is_on_unless_muted()
    {
        var marketing = new Notification("a@b.com", NotificationCategory.Marketing, NotificationSeverity.Info, "Sale", "...");
        var transactional = new Notification("a@b.com", NotificationCategory.Transactional, NotificationSeverity.Info, "Receipt", "...");

        Assert.False(NotificationPolicy.ShouldDeliver(marketing, new NotificationPreferences()));
        Assert.True(NotificationPolicy.ShouldDeliver(marketing, new NotificationPreferences(MarketingOptIn: true)));
        Assert.True(NotificationPolicy.ShouldDeliver(transactional, new NotificationPreferences()));
        Assert.False(NotificationPolicy.ShouldDeliver(transactional, new NotificationPreferences(TransactionalMuted: true)));
    }

    [Fact]
    public void Security_alert_content_is_minimal_and_carries_no_sensitive_payload()
    {
        const string secret = "BSB-062000-ACC-12345678";
        // The builder takes only action + a resource reference + outcome — there is nowhere to leak the value.
        var alert = SecurityAlert.ForAuditEvent("ops@tenant.com", "bank.reveal", "SupplierBankAccount", "b1", "Success");

        Assert.DoesNotContain(secret, alert.Body);
        Assert.Contains("bank.reveal", alert.Body);
        Assert.Contains("SupplierBankAccount/b1", alert.Body);
        Assert.Equal(NotificationSeverity.Critical, alert.Severity); // a successful reveal is critical
    }

    [Fact]
    public async Task A_channel_sends_through_its_transport()
    {
        var channel = new CapturingChannel();
        var note = new Notification("a@b.com", NotificationCategory.Transactional, NotificationSeverity.Info, "Hi", "Body");

        await channel.SendAsync(note, default);

        Assert.Equal(NotificationChannel.Email, channel.Channel);
        Assert.Same(note, channel.Sent);
    }

    private sealed class CapturingChannel : INotificationChannel
    {
        public NotificationChannel Channel => NotificationChannel.Email;

        public Notification? Sent { get; private set; }

        public Task SendAsync(Notification notification, CancellationToken ct)
        {
            Sent = notification;
            return Task.CompletedTask;
        }
    }
}
