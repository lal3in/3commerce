using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.Fulfillment.Domain.Carriers;
using ThreeCommerce.Fulfillment.Infrastructure.Carriers;

namespace ThreeCommerce.Fulfillment.Api.Endpoints;

/// <summary>
/// Shipping quotes (mt4_5). Drives the carrier rate seam (mt4_4) via ShippingQuoteService; the
/// per-group form returns a quote for each shipment group (one order, multiple shipments).
/// Anonymous — quotes are non-sensitive and needed at guest checkout; the gateway still fronts it.
/// </summary>
public static class ShippingEndpoints
{
    public static IEndpointRouteBuilder MapShipping(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/shipping").WithTags("Shipping");
        group.MapPost("/quote", Quote).AllowAnonymous();
        group.MapPost("/quote/groups", QuoteGroups).AllowAnonymous();
        group.MapPost("/revalidate", Revalidate).AllowAnonymous();
        return app;
    }

    private static async Task<Ok<QuoteResponse>> Quote(
        QuoteRequest request, ShippingQuoteService quotes, IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        var rates = await quotes.QuoteAsync(
            request.TenantId ?? DefaultTenantId(config), request.StorefrontId,
            new RateRequest(request.Origin.ToAddress(), request.Destination.ToAddress(), request.Parcel.ToParcel(), request.Service), ct);
        var expiresAt = clock.GetUtcNow().AddMinutes(QuoteTtlMinutes(config));
        return TypedResults.Ok(new QuoteResponse(rates.Select(ToDto).ToList(), expiresAt));
    }

    private static async Task<Ok<RevalidateResponse>> Revalidate(
        RevalidateRequest request, ShippingQuoteService quotes, IConfiguration config, CancellationToken ct)
    {
        var result = await quotes.RevalidateAsync(
            request.TenantId ?? DefaultTenantId(config), request.StorefrontId,
            new RateRequest(request.Origin.ToAddress(), request.Destination.ToAddress(), request.Parcel.ToParcel()),
            request.SelectedService, request.ExpectedAmountMinor, request.ExpiresAt, ct);
        return TypedResults.Ok(new RevalidateResponse(
            result.Outcome.ToString(), result.CurrentRate is null ? null : ToDto(result.CurrentRate)));
    }

    private static int QuoteTtlMinutes(IConfiguration config) =>
        int.TryParse(config["Shipping:QuoteTtlMinutes"], out var ttl) && ttl > 0 ? ttl : 30;

    private static async Task<Ok<GroupQuoteResponse>> QuoteGroups(
        GroupQuoteRequest request, ShippingQuoteService quotes, IConfiguration config, CancellationToken ct)
    {
        var groups = request.Groups
            .Select(g => (g.SourceKey, g.Origin.ToAddress(), g.Parcel.ToParcel()))
            .ToList();
        var result = await quotes.QuoteGroupsAsync(
            request.TenantId ?? DefaultTenantId(config), request.StorefrontId, request.Destination.ToAddress(), groups, ct);
        return TypedResults.Ok(new GroupQuoteResponse(
            result.Select(g => new GroupQuoteDto(g.SourceKey, g.Rates.Select(ToDto).ToList())).ToList()));
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static RateDto ToDto(CarrierRate r) =>
        new(r.Carrier.ToString(), r.Service, r.ServiceName, r.AmountMinor, r.Currency, r.EstimatedDays);
}

public record AddressDto(
    [property: Required] string Name,
    [property: Required] string Line1,
    [property: Required] string City,
    [property: Required] string Postcode,
    [property: Required, StringLength(2, MinimumLength = 2)] string Country)
{
    public ShipAddress ToAddress() => new(Name, Line1, City, Postcode, Country.ToUpperInvariant());
}

public record ParcelDto(
    [property: Range(0, int.MaxValue)] int WeightGrams, int LengthMm, int WidthMm, int HeightMm)
{
    public Parcel ToParcel() => new(WeightGrams, LengthMm, WidthMm, HeightMm);
}

public record QuoteRequest(Guid? TenantId, Guid? StorefrontId, AddressDto Origin, AddressDto Destination, ParcelDto Parcel, string? Service);

public record GroupRequestDto([property: Required] string SourceKey, AddressDto Origin, ParcelDto Parcel);

public record GroupQuoteRequest(Guid? TenantId, Guid? StorefrontId, AddressDto Destination, List<GroupRequestDto> Groups);

public record RateDto(string Carrier, string Service, string ServiceName, long AmountMinor, string Currency, int EstimatedDays);

public record QuoteResponse(IReadOnlyList<RateDto> Rates, DateTimeOffset ExpiresAt);

public record RevalidateRequest(
    Guid? TenantId, Guid? StorefrontId, AddressDto Origin, AddressDto Destination, ParcelDto Parcel,
    [property: Required] string SelectedService, long ExpectedAmountMinor, DateTimeOffset ExpiresAt);

public record RevalidateResponse(string Outcome, RateDto? CurrentRate);

public record GroupQuoteDto(string SourceKey, IReadOnlyList<RateDto> Rates);

public record GroupQuoteResponse(IReadOnlyList<GroupQuoteDto> Groups);
