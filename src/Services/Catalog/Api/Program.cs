using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.Catalog.Api.Endpoints;
using ThreeCommerce.Catalog.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceTelemetry("catalog");
builder.Services.AddApiProblemDetails();
builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddServiceBus<CatalogDbContext>(builder.Configuration);
builder.Services.AddServiceHealth<CatalogDbContext>();

var app = builder.Build();

app.UseApiProblemDetails();
app.MapServiceHealth();
app.MapPing();

app.Run();

public partial class Program;
