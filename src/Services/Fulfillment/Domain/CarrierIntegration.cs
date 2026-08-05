namespace ThreeCommerce.Fulfillment.Domain;

/// <summary>Supported carriers (mt4_3/mt4_4/mt4_10). Fake is keyless for tests/dev.</summary>
public enum CarrierCode
{
    Fake = 1,
    AustraliaPost = 2,
    Dhl = 3,
    FedEx = 4,
    Ups = 5,
    StarTrack = 6,
    PackAndSend = 7,
}

public enum CarrierIntegrationStatus
{
    Draft = 1,
    Active = 2,
    Suspended = 3,
    Disabled = 4,
}

/// <summary>
/// A storefront's configuration of a carrier (mt4_3 / ADR-0042). Carriers are always scoped to a
/// storefront — each storefront keeps its own independent carrier accounts (there is no tenant-level
/// default). Credentials live in the secret store — this holds only a reference. Owned by Fulfillment
/// (ADR-0027), tenant-scoped (ADR-0023).
/// </summary>
public sealed class CarrierIntegration
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }

    /// <summary>The storefront this carrier account belongs to (required — carriers are per-storefront).</summary>
    public Guid StorefrontId { get; init; }

    public CarrierCode Carrier { get; init; }

    /// <summary>Reference to the secret-store entry holding the API credentials — never the secret itself.</summary>
    public string? CredentialRef { get; private set; }

    public CarrierIntegrationStatus Status { get; private set; } = CarrierIntegrationStatus.Draft;
    public bool IsDefault { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsUsable => Status == CarrierIntegrationStatus.Active;

    private CarrierIntegration() { }

    public static CarrierIntegration Configure(
        Guid tenantId, Guid storefrontId, CarrierCode carrier, string? credentialRef, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty)
        {
            throw new FulfillmentRuleException("TenantId is required.");
        }

        if (storefrontId == Guid.Empty)
        {
            throw new FulfillmentRuleException("StorefrontId is required — carriers are configured per storefront.");
        }

        if (!Enum.IsDefined(carrier))
        {
            throw new FulfillmentRuleException($"Unknown carrier '{carrier}'.");
        }

        return new CarrierIntegration
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            StorefrontId = storefrontId,
            Carrier = carrier,
            CredentialRef = Normalize(credentialRef),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Clone this storefront-scoped account onto a duplicated storefront (mt4_3 / storefront duplication).
    /// Carries the carrier, credential reference, lifecycle status and default flag so the new storefront
    /// ships with the same, immediately-usable carrier accounts. The default flag is scoped to the new
    /// (tenant, storefront) pair, so copying it keeps exactly one default there.
    /// </summary>
    public CarrierIntegration CloneForStorefront(Guid newStorefrontId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            StorefrontId = newStorefrontId,
            Carrier = Carrier,
            CredentialRef = CredentialRef,
            Status = Status,
            IsDefault = IsDefault,
            CreatedAt = now,
            UpdatedAt = now,
        };

    /// <summary>A real carrier needs a credential reference before it can be used; Fake is keyless.</summary>
    public void Activate(DateTimeOffset now)
    {
        if (Carrier != CarrierCode.Fake && string.IsNullOrWhiteSpace(CredentialRef))
        {
            throw new FulfillmentRuleException("A real carrier needs a credential reference before activation.");
        }

        Status = CarrierIntegrationStatus.Active;
        UpdatedAt = now;
    }

    public void Suspend(DateTimeOffset now)
    {
        Status = CarrierIntegrationStatus.Suspended;
        UpdatedAt = now;
    }

    public void Disable(DateTimeOffset now)
    {
        Status = CarrierIntegrationStatus.Disabled;
        IsDefault = false;
        UpdatedAt = now;
    }

    public void SetCredentialRef(string? credentialRef, DateTimeOffset now)
    {
        CredentialRef = Normalize(credentialRef);
        UpdatedAt = now;
    }

    public void MarkDefault(DateTimeOffset now)
    {
        if (Status != CarrierIntegrationStatus.Active)
        {
            throw new FulfillmentRuleException("Only an active carrier can be made the default.");
        }

        IsDefault = true;
        UpdatedAt = now;
    }

    public void ClearDefault(DateTimeOffset now)
    {
        IsDefault = false;
        UpdatedAt = now;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
