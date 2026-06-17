using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Configuration;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.Fulfillment.Api.Endpoints;
using ThreeCommerce.Fulfillment.Infrastructure;
using ThreeCommerce.Fulfillment.Infrastructure.Consumers;

var builder = WebApplication.CreateBuilder(args);
builder.AddContainerConfig();

builder.AddServiceTelemetry("fulfillment");
builder.Services.AddApiProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddValidation();
builder.Services.AddDbContext<FulfillmentDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddServiceBus<FulfillmentDbContext>(builder.Configuration, bus =>
{
    bus.AddConsumer<OrderConfirmedConsumer>();
});
builder.Services.AddServiceHealth<FulfillmentDbContext>();
builder.Services.AddInternalClaimsAuth(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(TimeProvider.System);

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

app.Run();

public partial class Program;
