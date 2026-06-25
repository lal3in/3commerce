namespace ThreeCommerce.BuildingBlocks.Infrastructure.Audit;

/// <summary>
/// The audit coverage taxonomy (mt6_2): the categories worth recording — mutations, high-risk denied
/// attempts, sensitive reads, and field reveals. Ordinary reads are intentionally NOT a category
/// (don't audit every read). Sensitive-read / field-reveal factories take a field NAME + a reason and
/// never the value, so by construction no PII/secret lands in the audit payload (the mt6_2 GOTCHA).
/// </summary>
public static class AuditCategories
{
    /// <summary>A state change that succeeded.</summary>
    public static AuditDraft Mutation(
        Guid tenantId, Guid? actorId, string? actorRole, string resourceType, string resourceId, string action, string? summary = null) =>
        new(tenantId, action, resourceType, resourceId, AuditOutcome.Success, actorId, actorRole, summary);

    /// <summary>A high-risk action that was denied (e.g. a policy/maker-checker rejection). Carries the reason.</summary>
    public static AuditDraft DeniedAttempt(
        Guid tenantId, Guid? actorId, string? actorRole, string resourceType, string resourceId, string action, string reason) =>
        new(tenantId, action, resourceType, resourceId, AuditOutcome.Denied, actorId, actorRole, reason);

    /// <summary>A read of sensitive data — labelled by field name + the access reason, never the value.</summary>
    public static AuditDraft SensitiveRead(
        Guid tenantId, Guid? actorId, string? actorRole, string resourceType, string resourceId, string field, string reason) =>
        new(tenantId, $"sensitive_read.{field}", resourceType, resourceId, AuditOutcome.Success, actorId, actorRole, reason);

    /// <summary>An operator unmasking a sensitive field — labelled by field name + the reveal reason, never the value.</summary>
    public static AuditDraft FieldReveal(
        Guid tenantId, Guid? actorId, string? actorRole, string resourceType, string resourceId, string field, string reason) =>
        new(tenantId, $"field_reveal.{field}", resourceType, resourceId, AuditOutcome.Success, actorId, actorRole, reason);
}
