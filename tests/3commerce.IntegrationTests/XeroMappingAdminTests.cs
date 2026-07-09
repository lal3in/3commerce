using System.Net;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.IntegrationTests;

[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class XeroMappingAdminTests(Phase4Fixture fixture)
{
    private sealed record MappingDto(Guid Id, Guid TenantId, string Scope, Guid? StorefrontId, Guid? CategoryId,
        Guid? SupplierEntityId, Guid? ProductId, string LedgerAccountCode, string XeroAccountCode, bool Active);

    private HttpClient Admin()
    {
        var client = fixture.Payments.CreateClient();
        client.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), "admin"));
        return client;
    }

    [Fact]
    public async Task Xero_mapping_can_be_created_updated_listed_and_deleted()
    {
        var tenant = Guid.NewGuid();
        using var admin = Admin();

        var created = await (await admin.PostAsJsonAsync("/admin/xero/mappings", new
        {
            tenantId = tenant,
            scope = 1, // TenantDefault — enums are numeric on the wire (the string form is what this PR removes)
            ledgerAccountCode = Accounts.RevenueSales,
            xeroAccountCode = "200",
            active = true
        })).Content.ReadFromJsonAsync<MappingDto>();
        Assert.Equal("200", created!.XeroAccountCode);

        var updated = await (await admin.PutAsJsonAsync($"/admin/xero/mappings/{created.Id}", new
        {
            tenantId = tenant,
            scope = 1, // TenantDefault — enums are numeric on the wire (the string form is what this PR removes)
            ledgerAccountCode = Accounts.RevenueSales,
            xeroAccountCode = "201",
            active = true
        })).Content.ReadFromJsonAsync<MappingDto>();
        Assert.Equal("201", updated!.XeroAccountCode);

        var list = await admin.GetFromJsonAsync<List<MappingDto>>($"/admin/xero/mappings?tenantId={tenant}");
        Assert.Equal("201", Assert.Single(list!).XeroAccountCode);

        var delete = await admin.DeleteAsync($"/admin/xero/mappings/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        list = await admin.GetFromJsonAsync<List<MappingDto>>($"/admin/xero/mappings?tenantId={tenant}");
        Assert.Empty(list!);
    }
}
