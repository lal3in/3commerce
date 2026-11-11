namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// The billing address of one saved payment method (mem_2): where the card's statements go, which checkout uses as
/// the order's bill-to (ADR-0063 P3) when the shopper pays with that card. The same shape as Ordering's bill-to
/// (name, street, city, optional region, postcode, ISO country), so a prefilled bill-to is always one checkout accepts.
/// It is display/prefill data only: it never reaches the provider charge and never changes an amount.
/// </summary>
public sealed record CardBillingAddress(string Name, string Line1, string City, string? Region, string Postcode, string Country)
{
    public const string NameField = "Name";
    public const string Line1Field = "Line1";
    public const string CityField = "City";
    public const string RegionField = "Region";
    public const string PostcodeField = "Postcode";
    public const string CountryField = "Country";

    /// <summary>Column bounds — the same as Identity's address book, so an address saved in one fits the other.</summary>
    public const int NameMaxLength = 100;
    public const int Line1MaxLength = 200;
    public const int CityMaxLength = 100;
    public const int RegionMaxLength = 120;
    public const int PostcodeMaxLength = 20;

    /// <summary>
    /// Trims every part, upper-cases the country, drops a blank region, and refuses a missing name / street / city /
    /// postcode, an oversize part or a country that is not two letters — naming the field at fault.
    /// </summary>
    public static CardBillingAddress Create(string? name, string? line1, string? city, string? region, string? postcode, string? country)
    {
        var address = new CardBillingAddress(
            Required(name, NameField, NameMaxLength),
            Required(line1, Line1Field, Line1MaxLength),
            Required(city, CityField, CityMaxLength),
            string.IsNullOrWhiteSpace(region) ? null : Bounded(region.Trim(), RegionField, RegionMaxLength),
            Required(postcode, PostcodeField, PostcodeMaxLength),
            (country ?? string.Empty).Trim().ToUpperInvariant());
        if (address.Country.Length != 2 || !address.Country.All(char.IsAsciiLetterUpper))
        {
            throw new CardBillingAddressException("The country must be a 2-letter ISO code, e.g. AU.", CountryField);
        }

        return address;
    }

    private static string Required(string? value, string field, int max)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new CardBillingAddressException($"The billing address needs its {field.ToLowerInvariant()}.", field);
        }

        return Bounded(trimmed, field, max);
    }

    private static string Bounded(string value, string field, int max) =>
        value.Length <= max
            ? value
            : throw new CardBillingAddressException($"The {field.ToLowerInvariant()} can be at most {max} characters.", field);
}

/// <summary>A card billing address the profile refuses; <see cref="Field"/> names the part at fault.</summary>
public sealed class CardBillingAddressException(string message, string field) : Exception(message)
{
    public string Field { get; } = field;
}
