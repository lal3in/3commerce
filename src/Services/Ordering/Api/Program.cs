using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Configuration;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.Ordering.Api;
using ThreeCommerce.Ordering.Api.Endpoints;
using ThreeCommerce.Ordering.Infrastructure;
using ThreeCommerce.Ordering.Infrastructure.Consumers;
using ThreeCommerce.Ordering.Infrastructure.Projections;
using ThreeCommerce.Ordering.Infrastructure.Sagas;

var builder = WebApplication.CreateBuilder(args);
builder.AddContainerConfig();

builder.AddServiceTelemetry("ordering");
builder.Services.AddApiProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddValidation();
builder.Services.AddDbContext<OrderingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));

builder.Services.AddServiceBus<OrderingDbContext>(builder.Configuration,
    bus =>
    {
        bus.AddConsumer<PingRequestedConsumer>();
        bus.AddConsumer<ProductCopyConsumer>();
        bus.AddConsumer<OrderStatusConsumer>();
        bus.AddConsumer<GuestOrderAttachConsumer>();
        bus.AddSagaStateMachine<CheckoutStateMachine, CheckoutState>()
            .EntityFrameworkRepository(r =>
            {
                r.ExistingDbContext<OrderingDbContext>();
                r.UsePostgres();
            });
        bus.AddRequestClient<AuthorizePayment>();
    },
    // In-memory scheduler backs the saga's 30-min checkout timeout (no broker plugin needed).
    configureTransport: (_, cfg) => cfg.UseInMemoryScheduler());

builder.Services.AddServiceHealth<OrderingDbContext>();
builder.Services.AddInternalClaimsAuth(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CartService>();

ThreeCommerce.Ordering.Api.Endpoints.CartEndpoints.StoreCurrency = builder.Configuration["Store:Currency"] ?? "EUR";

var app = builder.Build();

app.UseApiProblemDetails();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapServiceHealth();
app.MapCart();
app.MapCheckout();
app.MapOrders();

app.Run();

public partial class Program;
