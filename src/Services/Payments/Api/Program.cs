using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.Payments.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceTelemetry("payments");
builder.Services.AddApiProblemDetails();
builder.Services.AddDbContext<PaymentsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddServiceBus<PaymentsDbContext>(builder.Configuration);
builder.Services.AddServiceHealth<PaymentsDbContext>();

var app = builder.Build();

app.UseApiProblemDetails();
app.MapServiceHealth();

app.Run();

public partial class Program;
