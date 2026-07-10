using MassTransit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Configuration;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.Workers.Notifications.Consumers;
using ThreeCommerce.Workers.Notifications.Email;

var builder = Host.CreateApplicationBuilder(args);
builder.AddContainerConfig();

builder.AddServiceTelemetry("notifications");

var storefrontBaseUrl = builder.Configuration["Storefront:BaseUrl"] ?? "http://localhost:3000";
builder.Services.AddSingleton(new EmailTemplates(storefrontBaseUrl));
builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
builder.Services.AddSingleton<IOrderEmailLookup, InMemoryOrderEmailLookup>();

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

var host = builder.Build();
host.Run();
