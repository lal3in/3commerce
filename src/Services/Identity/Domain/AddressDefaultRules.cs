namespace ThreeCommerce.Identity.Domain;

public static class AddressDefaultRules
{
    public static bool DefaultsConflict(AddressPurpose existing, AddressPurpose incoming) =>
        existing == AddressPurpose.Both || incoming == AddressPurpose.Both || existing == incoming;
}
