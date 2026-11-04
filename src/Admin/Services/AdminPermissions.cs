namespace ThreeCommerce.Admin.Services;

/// <summary>
/// The signed-in operator's effective permissions (Identity <c>GET /me/permissions</c>), loaded once per circuit.
/// Advisory UX only (ADR-0025): it hides or disables what the operator cannot do — every service still re-decides
/// through the PDP and answers 403 (refund_permission_enforcement). Fails closed: if the permissions can't be
/// read, nothing guarded is offered.
/// </summary>
public sealed class AdminPermissions(GatewayClient gateway)
{
    /// <summary>Issue refunds — Payments <c>POST /admin/refunds</c>, Support instant refund + RMA approval.</summary>
    public const string PaymentRefund = "payment.refund";

    private HashSet<string>? _granted;

    public async Task<bool> HasAsync(string permissionKey)
    {
        if (_granted is null)
        {
            MyPermissions? me;
            try
            {
                me = await gateway.GetAsync<MyPermissions>("/api/identity/me/permissions");
            }
            catch (HttpRequestException)
            {
                me = null;
            }

            if (me is null)
            {
                // Don't cache a failed read: deny now, and the next page load asks again.
                return false;
            }

            _granted = new HashSet<string>(me.Permissions, StringComparer.Ordinal);
        }

        return _granted.Contains(permissionKey);
    }

    private sealed record MyPermissions(bool IsPlatformAdmin, List<string> Permissions);
}
