using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Configuration;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.Fulfillment.Api.Endpoints;
using ThreeCommerce.Fulfillment.Domain.Carriers;
using ThreeCommerce.Fulfillment.Infrastructure;
using ThreeCommerce.Fulfillment.Infrastructure.Carriers;
using ThreeCommerce.Fulfillment.Infrastructure.Consumers;

var builder = WebApplication.CreateBuilder(args);
builder.AddContainerConfig();

builder.AddServiceTelemetry("fulfillment");
builder.Services.AddApiProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddValidation();
builder.Services.AddDbContext<FulfillmentDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database"), o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")));
builder.Services.AddServiceBus<FulfillmentDbContext>(builder.Configuration, bus =>
{
    bus.AddConsumer<OrderConfirmedConsumer>();
    bus.AddConsumer<RestockRequestedConsumer>();
});
builder.Services.AddServiceHealth<FulfillmentDbContext>();
builder.Services.AddInternalClaimsAuth(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AvailabilityNotifier>();
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<ReservationService>();
builder.Services.AddScoped<CarrierService>();
builder.Services.AddScoped<ShipmentService>();
builder.Services.AddScoped<FulfilmentProcessor>();
builder.Services.AddScoped<OrderHoldService>();

// Carrier adapters (mt4_4): Fake is keyless and serves all three seams; AusPost/DHL are rate
// adapters (sandbox placeholders until credentials onboard). They self-register by CarrierCode.
builder.Services.AddSingleton<FakeCarrierProvider>();
builder.Services.AddSingleton<ICarrierRateProvider>(sp => sp.GetRequiredService<FakeCarrierProvider>());
builder.Services.AddSingleton<ICarrierLabelProvider>(sp => sp.GetRequiredService<FakeCarrierProvider>());
builder.Services.AddSingleton<ICarrierTrackingProvider>(sp => sp.GetRequiredService<FakeCarrierProvider>());
builder.Services.AddSingleton<ICarrierRateProvider, AustraliaPostRateProvider>();
builder.Services.AddSingleton<ICarrierRateProvider, DhlRateProvider>();
builder.Services.AddSingleton<CarrierRegistry>();
builder.Services.AddScoped<ShippingQuoteService>();

// Dropship (mt4_4b): Fake supplier-order provider until real suppliers onboard (per-source creds).
builder.Services.AddSingleton<ThreeCommerce.Fulfillment.Domain.Dropship.ISupplierOrderProvider,
    ThreeCommerce.Fulfillment.Infrastructure.Dropship.FakeSupplierOrderProvider>();
builder.Services.AddScoped<ThreeCommerce.Fulfillment.Infrastructure.Dropship.SupplierOrderService>();
builder.Services.AddScoped<ThreeCommerce.Fulfillment.Infrastructure.Dropship.SupplierAvailabilityService>();

var app = builder.Build();

app.UseApiProblemDetails();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapServiceHealth();
app.MapAdminShipments();
app.MapInventory();
app.MapCarriers();
app.MapShipping();
app.MapDropship();
app.MapOrderHolds();

app.Run();

public partial class Program;
