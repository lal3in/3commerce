namespace ThreeCommerce.Entity.Domain;

/// <summary>The role a customer plays toward an entity in a customer↔entity link (mt2_7).</summary>
public enum CustomerEntityRole
{
    /// <summary>The customer is the account holder for this organisation entity.</summary>
    AccountHolder = 1,
    BillingContact = 2,
    AuthorisedBuyer = 3,
    Member = 4,
    Other = 5,
}

/// <summary>
/// A typed, effective-dated link between a tenant <b>customer</b> (an Identity principal) and an
/// <see cref="EntityRecord"/> (mt2_7). Many-to-many: a customer can relate to several entities
/// (e.g. billing contact for company A, authorised buyer for company B) and an entity can have
/// several customers. Mirrors <see cref="EntityRelationship"/>, but one side is a customer
/// principal rather than an entity. De-linking ends the link (sets <see cref="EffectiveTo"/>)
/// rather than deleting it, preserving history.
/// </summary>
public sealed class CustomerEntityLink
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid CustomerPrincipalId { get; init; }
    public Guid EntityId { get; init; }
    public CustomerEntityRole Role { get; init; }
    public DateTimeOffset EffectiveFrom { get; init; }
    public DateTimeOffset? EffectiveTo { get; private set; }
    public Guid LinkedByPrincipalId { get; init; }

    public bool IsActive => EffectiveTo is null;

    private CustomerEntityLink()
    {
    }

    public static CustomerEntityLink Create(
        Guid tenantId,
        Guid customerPrincipalId,
        Guid entityId,
        CustomerEntityRole role,
        Guid linkedByPrincipalId,
        DateTimeOffset effectiveFrom)
    {
        if (tenantId == Guid.Empty || customerPrincipalId == Guid.Empty || entityId == Guid.Empty)
        {
            throw new DomainRuleException("Customer link tenant, customer, and entity IDs are required.");
        }

        if (linkedByPrincipalId == Guid.Empty)
        {
            throw new DomainRuleException("The linking principal is required.");
        }

        if (!Enum.IsDefined(role))
        {
            throw new DomainRuleException($"Unknown customer-entity role '{role}'.");
        }

        return new CustomerEntityLink
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            CustomerPrincipalId = customerPrincipalId,
            EntityId = entityId,
            Role = role,
            LinkedByPrincipalId = linkedByPrincipalId,
            EffectiveFrom = effectiveFrom,
        };
    }

    public void End(DateTimeOffset now)
    {
        if (EffectiveTo is not null)
        {
            return;
        }

        EffectiveTo = now;
    }
}
