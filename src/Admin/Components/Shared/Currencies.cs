namespace ThreeCommerce.Admin.Components.Shared;

/// <summary>
/// The world's active currencies (ISO 4217). Used by <see cref="CurrencySelect"/> so currency is always
/// picked from a list, never free-typed. Code + English name; minor-unit handling stays in the services.
/// </summary>
public static class Currencies
{
    public sealed record Currency(string Code, string Name);

    /// <summary>A few common codes surfaced first in the dropdown for convenience.</summary>
    public static readonly string[] Common = ["EUR", "USD", "GBP", "AUD", "CAD", "JPY", "CHF", "CNY", "NZD"];

    public static readonly IReadOnlyList<Currency> All =
    [
        new("AED", "UAE Dirham"), new("AFN", "Afghan Afghani"), new("ALL", "Albanian Lek"),
        new("AMD", "Armenian Dram"), new("ANG", "Netherlands Antillean Guilder"), new("AOA", "Angolan Kwanza"),
        new("ARS", "Argentine Peso"), new("AUD", "Australian Dollar"), new("AWG", "Aruban Florin"),
        new("AZN", "Azerbaijani Manat"), new("BAM", "Bosnia-Herzegovina Convertible Mark"), new("BBD", "Barbadian Dollar"),
        new("BDT", "Bangladeshi Taka"), new("BGN", "Bulgarian Lev"), new("BHD", "Bahraini Dinar"),
        new("BIF", "Burundian Franc"), new("BMD", "Bermudan Dollar"), new("BND", "Brunei Dollar"),
        new("BOB", "Bolivian Boliviano"), new("BRL", "Brazilian Real"), new("BSD", "Bahamian Dollar"),
        new("BTN", "Bhutanese Ngultrum"), new("BWP", "Botswanan Pula"), new("BYN", "Belarusian Ruble"),
        new("BZD", "Belize Dollar"), new("CAD", "Canadian Dollar"), new("CDF", "Congolese Franc"),
        new("CHF", "Swiss Franc"), new("CLP", "Chilean Peso"), new("CNY", "Chinese Yuan"),
        new("COP", "Colombian Peso"), new("CRC", "Costa Rican Colón"), new("CUP", "Cuban Peso"),
        new("CVE", "Cape Verdean Escudo"), new("CZK", "Czech Koruna"), new("DJF", "Djiboutian Franc"),
        new("DKK", "Danish Krone"), new("DOP", "Dominican Peso"), new("DZD", "Algerian Dinar"),
        new("EGP", "Egyptian Pound"), new("ERN", "Eritrean Nakfa"), new("ETB", "Ethiopian Birr"),
        new("EUR", "Euro"), new("FJD", "Fijian Dollar"), new("FKP", "Falkland Islands Pound"),
        new("GBP", "British Pound"), new("GEL", "Georgian Lari"), new("GHS", "Ghanaian Cedi"),
        new("GIP", "Gibraltar Pound"), new("GMD", "Gambian Dalasi"), new("GNF", "Guinean Franc"),
        new("GTQ", "Guatemalan Quetzal"), new("GYD", "Guyanaese Dollar"), new("HKD", "Hong Kong Dollar"),
        new("HNL", "Honduran Lempira"), new("HRK", "Croatian Kuna"), new("HTG", "Haitian Gourde"),
        new("HUF", "Hungarian Forint"), new("IDR", "Indonesian Rupiah"), new("ILS", "Israeli New Shekel"),
        new("INR", "Indian Rupee"), new("IQD", "Iraqi Dinar"), new("IRR", "Iranian Rial"),
        new("ISK", "Icelandic Króna"), new("JMD", "Jamaican Dollar"), new("JOD", "Jordanian Dinar"),
        new("JPY", "Japanese Yen"), new("KES", "Kenyan Shilling"), new("KGS", "Kyrgystani Som"),
        new("KHR", "Cambodian Riel"), new("KMF", "Comorian Franc"), new("KPW", "North Korean Won"),
        new("KRW", "South Korean Won"), new("KWD", "Kuwaiti Dinar"), new("KYD", "Cayman Islands Dollar"),
        new("KZT", "Kazakhstani Tenge"), new("LAK", "Laotian Kip"), new("LBP", "Lebanese Pound"),
        new("LKR", "Sri Lankan Rupee"), new("LRD", "Liberian Dollar"), new("LSL", "Lesotho Loti"),
        new("LYD", "Libyan Dinar"), new("MAD", "Moroccan Dirham"), new("MDL", "Moldovan Leu"),
        new("MGA", "Malagasy Ariary"), new("MKD", "Macedonian Denar"), new("MMK", "Myanmar Kyat"),
        new("MNT", "Mongolian Tugrik"), new("MOP", "Macanese Pataca"), new("MRU", "Mauritanian Ouguiya"),
        new("MUR", "Mauritian Rupee"), new("MVR", "Maldivian Rufiyaa"), new("MWK", "Malawian Kwacha"),
        new("MXN", "Mexican Peso"), new("MYR", "Malaysian Ringgit"), new("MZN", "Mozambican Metical"),
        new("NAD", "Namibian Dollar"), new("NGN", "Nigerian Naira"), new("NIO", "Nicaraguan Córdoba"),
        new("NOK", "Norwegian Krone"), new("NPR", "Nepalese Rupee"), new("NZD", "New Zealand Dollar"),
        new("OMR", "Omani Rial"), new("PAB", "Panamanian Balboa"), new("PEN", "Peruvian Sol"),
        new("PGK", "Papua New Guinean Kina"), new("PHP", "Philippine Peso"), new("PKR", "Pakistani Rupee"),
        new("PLN", "Polish Złoty"), new("PYG", "Paraguayan Guarani"), new("QAR", "Qatari Rial"),
        new("RON", "Romanian Leu"), new("RSD", "Serbian Dinar"), new("RUB", "Russian Ruble"),
        new("RWF", "Rwandan Franc"), new("SAR", "Saudi Riyal"), new("SBD", "Solomon Islands Dollar"),
        new("SCR", "Seychellois Rupee"), new("SDG", "Sudanese Pound"), new("SEK", "Swedish Krona"),
        new("SGD", "Singapore Dollar"), new("SHP", "Saint Helena Pound"), new("SLE", "Sierra Leonean Leone"),
        new("SOS", "Somali Shilling"), new("SRD", "Surinamese Dollar"), new("SSP", "South Sudanese Pound"),
        new("STN", "São Tomé & Príncipe Dobra"), new("SYP", "Syrian Pound"), new("SZL", "Swazi Lilangeni"),
        new("THB", "Thai Baht"), new("TJS", "Tajikistani Somoni"), new("TMT", "Turkmenistani Manat"),
        new("TND", "Tunisian Dinar"), new("TOP", "Tongan Paʻanga"), new("TRY", "Turkish Lira"),
        new("TTD", "Trinidad & Tobago Dollar"), new("TWD", "New Taiwan Dollar"), new("TZS", "Tanzanian Shilling"),
        new("UAH", "Ukrainian Hryvnia"), new("UGX", "Ugandan Shilling"), new("USD", "US Dollar"),
        new("UYU", "Uruguayan Peso"), new("UZS", "Uzbekistani Som"), new("VED", "Venezuelan Bolívar"),
        new("VND", "Vietnamese Dong"), new("VUV", "Vanuatu Vatu"), new("WST", "Samoan Tala"),
        new("XAF", "Central African CFA Franc"), new("XCD", "East Caribbean Dollar"), new("XOF", "West African CFA Franc"),
        new("XPF", "CFP Franc"), new("YER", "Yemeni Rial"), new("ZAR", "South African Rand"),
        new("ZMW", "Zambian Kwacha"), new("ZWL", "Zimbabwean Dollar"),
    ];

    private static readonly HashSet<string> KnownCodes = All.Select(c => c.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string? code) => !string.IsNullOrWhiteSpace(code) && KnownCodes.Contains(code);
}
