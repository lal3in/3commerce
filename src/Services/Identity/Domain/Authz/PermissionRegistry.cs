namespace ThreeCommerce.Identity.Domain.Authz;

/// <summary>
/// The code-defined source of truth for permissions and the default role catalog (ADR-0025).
/// Permissions are NOT operator-editable; they are persisted from here at startup so role
/// mappings can FK to them. Each tenant is seeded with <see cref="DefaultRoles"/>, which
/// operators may then edit (add/remove permissions) or clone into new roles.
///
/// Keep this list authoritative: every enforceable action/field permission a service checks
/// must appear here, or a role can never grant it.
/// </summary>
public static class PermissionRegistry
{
    public sealed record PermissionDef(string Key, string Description, PermissionRiskLevel Risk);

    public sealed record RoleTemplate(string Key, string Name, string Description, bool IsBuiltIn, IReadOnlyList<string> PermissionKeys);

    public static IReadOnlyList<PermissionDef> Permissions { get; } =
    [
        // Platform / tenancy / authz
        new("tenant.view", "View tenant settings", PermissionRiskLevel.Low),
        new("tenant.manage", "Create/edit/suspend tenants and storefronts", PermissionRiskLevel.High),
        new("rbac.role.view", "View roles and permissions", PermissionRiskLevel.Low),
        new("rbac.role.manage", "Create/edit/clone/delete roles and assign permissions", PermissionRiskLevel.High),
        new("user.view", "View users and memberships", PermissionRiskLevel.Low),
        new("user.manage", "Invite/edit/lock users; assign roles", PermissionRiskLevel.High),
        new("serviceaccount.manage", "Create/rotate/revoke service accounts", PermissionRiskLevel.High),
        new("audit.view", "View audit log", PermissionRiskLevel.Low),
        new("mission_control.view", "View the operations mission-control console", PermissionRiskLevel.Low),

        // Catalog / merchandising
        new("catalog.product.view", "View products", PermissionRiskLevel.Low),
        new("catalog.product.edit", "Create/edit products, variants, pricing", PermissionRiskLevel.Medium),
        new("catalog.product.publish", "Publish products to storefronts", PermissionRiskLevel.Medium),

        // Orders / fulfillment
        new("order.view", "View orders", PermissionRiskLevel.Low),
        new("order.manage", "Manage orders and holds", PermissionRiskLevel.Medium),
        new("fulfillment.view", "View inventory/shipments", PermissionRiskLevel.Low),
        new("fulfillment.manage", "Manage inventory, carriers, shipments", PermissionRiskLevel.Medium),

        // Money
        new("payment.view", "View payments", PermissionRiskLevel.Low),
        new("payment.refund", "Issue refunds", PermissionRiskLevel.High),
        new("ledger.view", "View the double-entry ledger", PermissionRiskLevel.Low),

        // Suppliers
        new("supplier.view", "View suppliers", PermissionRiskLevel.Low),
        new("supplier.manage", "Onboard/edit suppliers and payout policy", PermissionRiskLevel.High),

        // Support
        new("support.ticket.view", "View support tickets", PermissionRiskLevel.Low),
        new("support.ticket.manage", "Manage support tickets", PermissionRiskLevel.Medium),
        new("rma.manage", "Approve/process RMAs", PermissionRiskLevel.Medium),
    ];

    public static IReadOnlyList<string> AllPermissionKeys { get; } = Permissions.Select(p => p.Key).ToArray();

    private static readonly HashSet<string> KeySet = AllPermissionKeys.ToHashSet(StringComparer.Ordinal);

    public static bool IsKnown(string key) => KeySet.Contains(key);

    public static IReadOnlyList<RoleTemplate> DefaultRoles { get; } =
    [
        new("admin", "Administrator", "Full access to the tenant.", IsBuiltIn: true, AllPermissionKeys),
        new("ops", "Operations", "Orders, fulfillment, and mission control.", IsBuiltIn: true,
            ["order.view", "order.manage", "fulfillment.view", "fulfillment.manage", "mission_control.view", "support.ticket.view"]),
        new("finance", "Finance", "Payments, refunds, ledger, and audit.", IsBuiltIn: true,
            ["payment.view", "payment.refund", "ledger.view", "audit.view", "order.view"]),
        new("support", "Support", "Support tickets and returns.", IsBuiltIn: true,
            ["support.ticket.view", "support.ticket.manage", "rma.manage", "order.view"]),
        new("merchandiser", "Merchandiser", "Catalog and product publishing.", IsBuiltIn: true,
            ["catalog.product.view", "catalog.product.edit", "catalog.product.publish"]),
        // Built-in storefront shopper role; no back-office permissions.
        new("customer", "Customer", "Storefront shopper.", IsBuiltIn: true, []),
    ];
}
