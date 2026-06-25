using Microsoft.EntityFrameworkCore;
using Npgsql;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Configuration;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.Catalog.Api.Endpoints;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;
using ThreeCommerce.Catalog.Infrastructure.Importers;
using ThreeCommerce.Catalog.Infrastructure.Search;

var builder = WebApplication.CreateBuilder(args);
builder.AddContainerConfig();

builder.AddServiceTelemetry("catalog");
builder.Services.AddApiProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddValidation();
// Dynamic JSON lets Npgsql serialize Dictionary/List POCO properties to jsonb.
var catalogDataSource = new NpgsqlDataSourceBuilder(builder.Configuration.GetConnectionString("Database"));
catalogDataSource.EnableDynamicJson();
builder.Services.AddSingleton(catalogDataSource.Build());
builder.Services.AddDbContext<CatalogDbContext>((sp, options) =>
    options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")));
builder.Services.AddServiceBus<CatalogDbContext>(builder.Configuration, bus =>
{
    bus.AddConsumer<ThreeCommerce.Catalog.Infrastructure.Consumers.InventoryAvailabilityConsumer>();
});
builder.Services.AddServiceHealth<CatalogDbContext>();
builder.Services.AddInternalClaimsAuth(builder.Configuration, builder.Environment);

builder.Services.AddScoped<ISupplierImporter, SampleDataImporter>();
builder.Services.AddScoped<ISearchProvider, PostgresSearchProvider>();
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
app.MapPing();
app.MapProducts();
app.MapAdmin();
app.MapStorefrontAdmin();
app.MapOffers();

app.Run();

public partial class Program;
