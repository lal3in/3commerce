using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Configuration;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.Payments.Api;
using ThreeCommerce.Payments.Api.Endpoints;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Xero;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Consumers;
using ThreeCommerce.Payments.Infrastructure.Payments;
using ThreeCommerce.Payments.Infrastructure.Stripe;
using ThreeCommerce.Payments.Infrastructure.Xero;

var builder = WebApplication.CreateBuilder(args);
builder.AddContainerConfig();

builder.AddServiceTelemetry("payments");
builder.Services.AddApiProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddValidation();
builder.Services.AddDbContext<PaymentsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database"), o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")));
builder.Services.AddServiceBus<PaymentsDbContext>(builder.Configuration, bus =>
{
    bus.AddConsumer<AuthorizePaymentConsumer>();
    bus.AddConsumer<ExecuteRefundConsumer>();
    bus.AddConsumer<RefundPostingConsumer>();
});
builder.Services.AddServiceHealth<PaymentsDbContext>();
builder.Services.AddInternalClaimsAuth(builder.Configuration, builder.Environment);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ITaxStrategy, FlatRateTaxStrategy>();
builder.Services.AddScoped<PaymentEventProcessor>();
builder.Services.AddSingleton<IXeroClient, LoggingXeroClient>();
builder.Services.AddScoped<DailyJournalJob>();

// Payment provider: real Stripe when keys are set, deterministic fake otherwise (ADR-0015).
if (!string.IsNullOrEmpty(builder.Configuration["Stripe:SecretKey"]))
{
    builder.Services.AddSingleton<IPaymentProvider, StripePaymentProvider>();
}
else
{
    builder.Services.AddSingleton<IPaymentProvider, FakePaymentProvider>();
}

var app = builder.Build();

app.UseApiProblemDetails();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapServiceHealth();
app.MapWebhooks();
app.MapAdmin();
app.MapAdminXero();
app.MapCustomerPaymentMethods();

await ChartOfAccountsSeeder.SeedAsync(app);

app.Run();

public partial class Program;
