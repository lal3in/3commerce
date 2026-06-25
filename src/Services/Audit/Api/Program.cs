using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Audit.Api.Endpoints;
using ThreeCommerce.Audit.Infrastructure;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Configuration;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;

var builder = WebApplication.CreateBuilder(args);
builder.AddContainerConfig();

builder.AddServiceTelemetry("audit");
builder.Services.AddApiProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddValidation();
builder.Services.AddDbContext<AuditDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database"), o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")));
builder.Services.AddServiceBus<AuditDbContext>(builder.Configuration, bus => bus.AddConsumer<AuditEntryRecordedConsumer>());
builder.Services.AddServiceHealth<AuditDbContext>();
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
app.MapAuditSearch();
app.Run();

public partial class Program;
