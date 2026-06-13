using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.Identity.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceTelemetry("identity");
builder.Services.AddApiProblemDetails();
builder.Services.AddDbContext<IdentityDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddServiceBus<IdentityDbContext>(builder.Configuration);
builder.Services.AddServiceHealth<IdentityDbContext>();

var app = builder.Build();

app.UseApiProblemDetails();
app.MapServiceHealth();

app.Run();

public partial class Program;
