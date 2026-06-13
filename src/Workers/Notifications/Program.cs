using MassTransit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.Workers.Notifications.Consumers;
using ThreeCommerce.Workers.Notifications.Email;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceTelemetry("notifications");

var storefrontBaseUrl = builder.Configuration["Storefront:BaseUrl"] ?? "http://localhost:3000";
builder.Services.AddSingleton(new EmailTemplates(storefrontBaseUrl));
builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();

builder.Services.AddServiceBus(builder.Configuration, bus =>
{
    bus.AddConsumer<PongRespondedConsumer>();
    bus.AddConsumer<UserRegisteredConsumer>();
    bus.AddConsumer<PasswordResetRequestedConsumer>();
});

var host = builder.Build();
host.Run();
