using System.Text.RegularExpressions;

namespace ThreeCommerce.Entity.Domain;

/// <summary>
/// A place the tenant's seller of record (its <see cref="EntityRoleKind.TenantOwner"/> entity) does business
/// from — an address plus the tax jurisdiction it is established in (ADR-0063 § 7.3, P2). A storefront binds
/// to one by <see cref="Key"/> (Catalog <c>Storefront.SellerEstablishmentKey</c>), which is why the key is
/// immutable once created and unique within the entity; the address, label and jurisdiction can be edited.
/// Keyed only by <see cref="EntityId"/>, so its RLS policy joins to the parent entity's TenantId (ADR-0024).
/// </summary>
public sealed partial class SellerEstablishment
{
    public Guid Id { get; init; }
    public Guid EntityId { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Label { get; private set; } = string.Empty;
    public string Line1 { get; private set; } = string.Empty;
    public string? Line2 { get; private set; }
    public string City { get; private set; } = string.Empty;
    public string? Region { get; private set; }
    public string Postcode { get; private set; } = string.Empty;
    public string CountryCode { get; private set; } = string.Empty;

    /// <summary>ISO country with optional subdivision (<c>DE</c>, <c>US-CA</c>); its country is <see cref="CountryCode"/>.</summary>
    public string Jurisdiction { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private SellerEstablishment()
    {
    }

    internal static SellerEstablishment Create(Guid entityId, string key, SellerEstablishmentDetails details, DateTimeOffset now)
    {
        var establishment = new SellerEstablishment
        {
            Id = Guid.CreateVersion7(),
            EntityId = entityId,
            Key = NormalizeKey(key),
            CreatedAt = now,
        };
        establishment.Apply(details, now);
        return establishment;
    }

    internal void Apply(SellerEstablishmentDetails details, DateTimeOffset now)
    {
        Label = Required(details.Label, 2, 120, nameof(details.Label));
        Line1 = Required(details.Line1, 2, 200, nameof(details.Line1));
        Line2 = Optional(details.Line2, 200, nameof(details.Line2));
        City = Required(details.City, 2, 120, nameof(details.City));
        Region = Optional(details.Region, 120, nameof(details.Region));
        Postcode = Required(details.Postcode, 2, 32, nameof(details.Postcode));

        var country = (details.CountryCode ?? string.Empty).Trim().ToUpperInvariant();
        if (country.Length != 2 || !country.All(char.IsAsciiLetterUpper))
        {
            throw new DomainRuleException("CountryCode must be ISO 3166-1 alpha-2.", nameof(details.CountryCode));
        }

        // The jurisdiction defaults to the address country; a region narrows it (US-CA) but can never move
        // the establishment to another country than its own address.
        var jurisdiction = string.IsNullOrWhiteSpace(details.Jurisdiction)
            ? country
            : TaxIdentifierFormats.NormalizeJurisdiction(details.Jurisdiction, nameof(details.Jurisdiction));
        if (jurisdiction[..2] != country)
        {
            throw new DomainRuleException(
                $"Jurisdiction '{jurisdiction}' must be in the establishment's country ({country}).", nameof(details.Jurisdiction));
        }

        CountryCode = country;
        Jurisdiction = jurisdiction;
        UpdatedAt = now;
    }

    /// <summary>A key is a lowercase slug (2–40 chars of a–z, 0–9, '-'), e.g. <c>sydney-hq</c>.</summary>
    public static string NormalizeKey(string? key)
    {
        var normalized = (key ?? string.Empty).Trim().ToLowerInvariant();
        if (!KeyShape().IsMatch(normalized))
        {
            throw new DomainRuleException(
                "Key must be 2–40 lowercase letters, digits or hyphens, starting with a letter or digit (e.g. sydney-hq).", nameof(Key));
        }

        return normalized;
    }

    private static string Required(string? value, int min, int max, string field)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length < min || normalized.Length > max)
        {
            throw new DomainRuleException($"{field} must be between {min} and {max} characters.", field);
        }

        return normalized;
    }

    private static string? Optional(string? value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > max)
        {
            throw new DomainRuleException($"{field} must be at most {max} characters.", field);
        }

        return normalized;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{1,39}$")]
    private static partial Regex KeyShape();
}

/// <summary>The editable part of a <see cref="SellerEstablishment"/>.</summary>
public sealed record SellerEstablishmentDetails(
    string? Label,
    string? Line1,
    string? Line2,
    string? City,
    string? Region,
    string? Postcode,
    string? CountryCode,
    string? Jurisdiction);
