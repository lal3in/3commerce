using MassTransit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.Workers.Notifications;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceTelemetry("notifications");
builder.Services.AddServiceBus(builder.Configuration,
    bus => bus.AddConsumer<PongRespondedConsumer>());

var host = builder.Build();
host.Run();
