using ThreeCommerce.Entity.Domain;

namespace ThreeCommerce.Entity.Tests;

public class CustomerEntityLinkTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly Guid EntityId = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();

    private static CustomerEntityLink New(CustomerEntityRole role = CustomerEntityRole.BillingContact) =>
        CustomerEntityLink.Create(Tenant, Customer, EntityId, role, Actor, DateTimeOffset.UtcNow);

    [Fact]
    public void Create_starts_active()
    {
        var link = New();
        Assert.True(link.IsActive);
        Assert.Null(link.EffectiveTo);
        Assert.Equal(Customer, link.CustomerPrincipalId);
        Assert.Equal(EntityId, link.EntityId);
        Assert.Equal(Actor, link.LinkedByPrincipalId);
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void Create_requires_all_ids(bool tenant, bool customer, bool entity, bool actor)
    {
        Assert.Throws<DomainRuleException>(() => CustomerEntityLink.Create(
            tenant ? Tenant : Guid.Empty,
            customer ? Customer : Guid.Empty,
            entity ? EntityId : Guid.Empty,
            CustomerEntityRole.Member,
            actor ? Actor : Guid.Empty,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Create_rejects_an_unknown_role()
    {
        Assert.Throws<DomainRuleException>(() => CustomerEntityLink.Create(
            Tenant, Customer, EntityId, (CustomerEntityRole)99, Actor, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Ending_marks_inactive()
    {
        var link = New();
        var now = DateTimeOffset.UtcNow;
        link.End(now);
        Assert.False(link.IsActive);
        Assert.Equal(now, link.EffectiveTo);
    }

    [Fact]
    public void Ending_is_idempotent()
    {
        var link = New();
        var first = DateTimeOffset.UtcNow;
        link.End(first);
        link.End(first.AddHours(1));
        Assert.Equal(first, link.EffectiveTo);
    }
}
