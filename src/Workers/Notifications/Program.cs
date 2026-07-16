using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Configuration;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.Workers.Notifications.Api;
using ThreeCommerce.Workers.Notifications.Consumers;
using ThreeCommerce.Workers.Notifications.Email;
using ThreeCommerce.Workers.Notifications.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.AddContainerConfig();
builder.AddServiceTelemetry("notifications");
builder.Services.AddApiProblemDetails();
builder.Services.AddOpenApi();

builder.Services.AddSingleton(TimeProvider.System);

var storefrontBaseUrl = builder.Configuration["Storefront:BaseUrl"] ?? "http://localhost:3000";
builder.Services.AddSingleton(new EmailTemplates(storefrontBaseUrl));
// The real (dev/sandbox logging) sender, wrapped by a recorder that writes every attempt to the
// delivery log so Mission Control can monitor the notification pipeline (mc_proc_4).
builder.Services.AddSingleton<LoggingEmailSender>();
builder.Services.AddSingleton<IEmailSender>(sp => new RecordingEmailSender(
    sp.GetRequiredService<LoggingEmailSender>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<RecordingEmailSender>>()));
builder.Services.AddSingleton<IOrderEmailLookup, InMemoryOrderEmailLookup>();

builder.Services.AddDbContext<NotificationsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database"), o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")));

builder.Services.AddServiceBus(builder.Configuration, bus =>
{
    bus.AddConsumer<PongRespondedConsumer>();
    bus.AddConsumer<UserRegisteredConsumer>();
    bus.AddConsumer<PasswordResetRequestedConsumer>();
    bus.AddConsumer<OrderConfirmedConsumer>();
    bus.AddConsumer<TrackingAssignedConsumer>();
    bus.AddConsumer<TicketOpenedConsumer>();
    bus.AddConsumer<RmaStateChangedConsumer>();
    bus.AddConsumer<MockPaymentCapturedConsumer>(); // pay_3: TEST-ONLY mock-payment payload email
});
builder.Services.AddServiceHealth<NotificationsDbContext>();
builder.Services.AddInternalClaimsAuth(builder.Configuration, builder.Environment);

var app = builder.Build();
app.UseApiProblemDetails();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapServiceHealth();
app.MapNotifications();

// The notifications DB isn't part of dev-up's migrate loop (this is a worker-turned-service), so apply
// pending migrations at startup — idempotent, and keeps the delivery-log table present wherever it runs.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.MigrateAsync();
}

app.Run();

public partial class Program;
