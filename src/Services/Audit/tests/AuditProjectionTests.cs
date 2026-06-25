using ThreeCommerce.Audit.Domain;

namespace ThreeCommerce.Audit.Tests;

public class AuditProjectionTests
{
    [Fact]
    public void Projection_holds_the_projected_fields()
    {
        var entry = new AuditProjection
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Sequence = 7,
            OccurredAt = DateTimeOffset.UnixEpoch,
            Action = "supplier.change_request.approved",
            ResourceType = "SupplierChangeRequest",
            ResourceId = "r1",
            Outcome = "Success",
            Hash = "abc",
        };
        Assert.Equal(7, entry.Sequence);
        Assert.Equal("Success", entry.Outcome);
    }
}
