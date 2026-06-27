namespace ThreeCommerce.Payments.Domain.Xero;

public enum XeroMappingScope
{
    TenantDefault = 1,
    Storefront = 2,
    Category = 3,
    Supplier = 4,
    Product = 5,
}

public class XeroAccountMapping
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public XeroMappingScope Scope { get; set; }
    public Guid? StorefrontId { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? SupplierEntityId { get; set; }
    public Guid? ProductId { get; set; }
    public required string LedgerAccountCode { get; set; }
    public required string XeroAccountCode { get; set; }
    public bool Active { get; set; } = true;

    public bool Matches(XeroMappingContext context, string ledgerAccountCode) =>
        Active && TenantId == context.TenantId && LedgerAccountCode == ledgerAccountCode && Scope switch
        {
            XeroMappingScope.Product => ProductId == context.ProductId,
            XeroMappingScope.Supplier => SupplierEntityId == context.SupplierEntityId,
            XeroMappingScope.Category => CategoryId == context.CategoryId,
            XeroMappingScope.Storefront => StorefrontId == context.StorefrontId,
            XeroMappingScope.TenantDefault => true,
            _ => false,
        };
}

public sealed record XeroMappingContext(
    Guid TenantId,
    Guid? StorefrontId = null,
    Guid? CategoryId = null,
    Guid? SupplierEntityId = null,
    Guid? ProductId = null);

public sealed class XeroMappingResolver
{
    private static readonly XeroMappingScope[] Precedence =
    [
        XeroMappingScope.Product,
        XeroMappingScope.Supplier,
        XeroMappingScope.Category,
        XeroMappingScope.Storefront,
        XeroMappingScope.TenantDefault,
    ];

    public string Resolve(string ledgerAccountCode, XeroMappingContext context, IReadOnlyList<XeroAccountMapping> mappings)
    {
        foreach (var scope in Precedence)
        {
            var mapping = mappings.FirstOrDefault(m => m.Scope == scope && m.Matches(context, ledgerAccountCode));
            if (mapping is not null)
            {
                return mapping.XeroAccountCode;
            }
        }

        throw new XeroMappingRuleException($"No Xero mapping for ledger account '{ledgerAccountCode}'.");
    }
}

public sealed class XeroMappingRuleException(string message) : Exception(message);
