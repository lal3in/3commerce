using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.Fulfillment.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceTelemetry("fulfillment");
builder.Services.AddApiProblemDetails();
builder.Services.AddDbContext<FulfillmentDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddServiceBus<FulfillmentDbContext>(builder.Configuration);
builder.Services.AddServiceHealth<FulfillmentDbContext>();

var app = builder.Build();

app.UseApiProblemDetails();
app.MapServiceHealth();

app.Run();

public partial class Program;
