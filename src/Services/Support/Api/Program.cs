using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.Support.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceTelemetry("support");
builder.Services.AddApiProblemDetails();
builder.Services.AddDbContext<SupportDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddServiceBus<SupportDbContext>(builder.Configuration);
builder.Services.AddServiceHealth<SupportDbContext>();

var app = builder.Build();

app.UseApiProblemDetails();
app.MapServiceHealth();

app.Run();

public partial class Program;
