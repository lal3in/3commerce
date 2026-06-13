using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.Ordering.Infrastructure;
using ThreeCommerce.Ordering.Infrastructure.Consumers;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceTelemetry("ordering");
builder.Services.AddApiProblemDetails();
builder.Services.AddDbContext<OrderingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddServiceBus<OrderingDbContext>(builder.Configuration,
    bus => bus.AddConsumer<PingRequestedConsumer>());
builder.Services.AddServiceHealth<OrderingDbContext>();

var app = builder.Build();

app.UseApiProblemDetails();
app.MapServiceHealth();

app.Run();

public partial class Program;
