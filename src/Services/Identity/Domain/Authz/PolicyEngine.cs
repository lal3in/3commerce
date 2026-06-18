namespace ThreeCommerce.Identity.Domain.Authz;

/// <summary>
/// The Policy Decision Point core (ADR-0025): pure functions that turn an
/// <see cref="AuthorizationContext"/> into action and field decisions. The Identity PDP API
/// resolves a principal's effective permissions and calls these; services (PEP) enforce the
/// returned decisions server-side. No I/O here — deterministic and unit-testable.
/// </summary>
public static class PolicyEngine
{
    /// <summary>
    /// Decide one action. A MasterGlobal is allowed everything but must supply a reason for
    /// high-risk actions; ordinary staff need the granted permission, and a high-risk action
    /// they hold requires maker-checker approval.
    /// </summary>
    public static ActionDecision DecideAction(AuthorizationContext ctx, string permissionKey)
    {
        var risk = PermissionRegistry.RiskOf(permissionKey);
        var allowed = ctx.IsPlatformAdmin || ctx.Has(permissionKey);
        var highRisk = risk == PermissionRiskLevel.High;

        var requiresReason = allowed && ctx.IsPlatformAdmin && highRisk;
        var requiresApproval = allowed && !ctx.IsPlatformAdmin && highRisk;

        return new ActionDecision(permissionKey, allowed, risk, requiresReason, requiresApproval);
    }

    /// <summary>Batched action decisions (one request → many decisions, ADR-0025 hot-path rule).</summary>
    public static IReadOnlyList<ActionDecision> DecideActions(AuthorizationContext ctx, IEnumerable<string> permissionKeys) =>
        permissionKeys.Select(k => DecideAction(ctx, k)).ToArray();

    /// <summary>Decide access to one field given its policy.</summary>
    public static FieldDecision DecideField(AuthorizationContext ctx, FieldPolicy policy)
    {
        var canView = policy.ViewPermission is null || ctx.IsPlatformAdmin || ctx.Has(policy.ViewPermission);
        if (!canView)
        {
            return new FieldDecision(policy.Field, FieldAccess.Hidden, RequiresRevealReason: false);
        }

        // Sensitive fields are masked even for viewers — the real value needs an explicit reveal
        // (with reason + audit), MasterGlobal included.
        if (policy.Sensitive)
        {
            return new FieldDecision(policy.Field, FieldAccess.Masked, RequiresRevealReason: true);
        }

        var canEdit = policy.EditPermission is not null && (ctx.IsPlatformAdmin || ctx.Has(policy.EditPermission));
        return new FieldDecision(policy.Field, canEdit ? FieldAccess.Editable : FieldAccess.ReadOnly, RequiresRevealReason: false);
    }

    /// <summary>Batched field decisions.</summary>
    public static IReadOnlyList<FieldDecision> DecideFields(AuthorizationContext ctx, IEnumerable<FieldPolicy> policies) =>
        policies.Select(p => DecideField(ctx, p)).ToArray();
}
