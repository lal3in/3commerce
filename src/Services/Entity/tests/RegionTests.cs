using ThreeCommerce.BuildingBlocks.Infrastructure.Governance;

namespace ThreeCommerce.Entity.Tests;

public class RegionTests
{
    [Fact]
    public void A_tenant_home_region_cannot_be_moved()
    {
        var region = new TenantRegion(Guid.NewGuid(), DataRegion.AustraliaEast);
        Assert.False(region.CanMoveTo(DataRegion.AustraliaEast)); // no cross-region migration (GOTCHA)
    }

    [Fact]
    public void Ledger_and_audit_are_retained_indefinitely_regardless_of_age()
    {
        var ancient = TimeSpan.FromDays(365 * 50);
        Assert.Equal(RetentionAction.Retain, RetentionPolicy.Resolve(DataCategory.LedgerEntry, ancient));
        Assert.Equal(RetentionAction.Retain, RetentionPolicy.Resolve(DataCategory.AuditLog, ancient));
    }

    [Fact]
    public void Operational_logs_purge_after_their_window()
    {
        Assert.Equal(RetentionAction.Retain, RetentionPolicy.Resolve(DataCategory.OperationalLog, TimeSpan.FromDays(30)));
        Assert.Equal(RetentionAction.Purge, RetentionPolicy.Resolve(DataCategory.OperationalLog, TimeSpan.FromDays(120)));
    }

    [Fact]
    public void Orders_and_pii_redact_after_seven_years_but_are_kept_before()
    {
        var sixYears = TimeSpan.FromDays(365 * 6);
        var eightYears = TimeSpan.FromDays(365 * 8);

        Assert.Equal(RetentionAction.Retain, RetentionPolicy.Resolve(DataCategory.OrderRecord, sixYears));
        Assert.Equal(RetentionAction.Redact, RetentionPolicy.Resolve(DataCategory.OrderRecord, eightYears));
        Assert.Equal(RetentionAction.Redact, RetentionPolicy.Resolve(DataCategory.CustomerPii, eightYears));
    }
}
