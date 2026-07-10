using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.Workers.Notifications.Consumers;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// pay_3 end-to-end (ADR-0039): a LocalMock checkout publishes the TEST-ONLY MockPaymentCaptured
/// event on the real bus, the Notifications consumer renders the "TEST ONLY / MOCK PAYMENT" email
/// to Payments:MockEmailTo with the redacted payload, and the same order still funnels through
/// /dev/simulate-payment into the ledger Sale. Plus the Production boot refusal: a host configured
/// with Payments:AllowMockEmail=true outside Development fails to start.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class MockEmailCaptureTests(Phase3Fixture fixture)
{
    private sealed record CheckoutResponseDto(Guid OrderId, string ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor, long GrossMinor, string Currency, string? Message);
    private sealed record StatusDto(Guid Id, string Status);

    private sealed class RecordingEmailSender : IEmailSender
    {
        public ConcurrentQueue<EmailMessage> Sent { get; } = new();

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            Sent.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task LocalMock_checkout_sends_the_test_only_email_with_redacted_payload_and_still_posts_the_ledger_sale()
    {
        // A Notifications-worker stand-in on the fixture's real RabbitMQ: the same consumer +
        // templates the worker registers, with a recording sender instead of the logging one.
        var recorder = new RecordingEmailSender();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:RabbitMq"] = fixture.RabbitMqUri;
        builder.Configuration["Payments:MockEmailTo"] = "qa-payments@test.local";
        builder.Services.AddSingleton(new EmailTemplates("http://localhost:3000"));
        builder.Services.AddSingleton<IEmailSender>(recorder);
        builder.Services.AddServiceBus(builder.Configuration, bus => bus.AddConsumer<MockPaymentCapturedConsumer>());
        using var notifications = builder.Build();
        await notifications.StartAsync();
        try
        {
            var productId = await fixture.SeedProductAsync(10_000);
            using var shopper = fixture.Ordering.CreateClient();
            await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
            var checkout = await shopper.PostAsJsonAsync("/checkout", new
            {
                email = "buyer@example.com",
                shippingAddress = new { name = "B", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
            });
            checkout.EnsureSuccessStatusCode();
            var order = (await checkout.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;

            // The TEST-ONLY email arrives via the bus (outbox → RabbitMQ → consumer → sender).
            var email = await WaitForEmailAsync(recorder, order.OrderId);
            Assert.Equal("qa-payments@test.local", email.To);
            Assert.StartsWith("[TEST ONLY / MOCK PAYMENT]", email.Subject);
            Assert.Contains("TEST ONLY / MOCK PAYMENT", email.Body);
            Assert.Contains("NO money moved", email.Body);
            Assert.Contains(order.OrderId.ToString(), email.Body);
            Assert.Contains("Mode: LocalMock", email.Body);
            Assert.Contains("Scenario:   Success", email.Body);
            Assert.Contains("\"amountMinor\"", email.Body);   // the redacted payload is embedded
            Assert.Contains("\"idempotencyKey\"", email.Body);
            Assert.DoesNotContain("client_secret", email.Body); // no secrets in the payload

            // The same order still funnels through simulate → PaymentEventProcessor → ledger Sale.
            await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
            await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");
            Assert.Equal(0, await fixture.TrialBalanceAsync());
        }
        finally
        {
            await notifications.StopAsync();
        }
    }

    [Fact]
    public void Production_host_with_AllowMockEmail_refuses_to_boot()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var factory = new WebApplicationFactory<ThreeCommerce.Payments.Api.IApiMarker>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(Environments.Production);
            b.UseSetting("InternalAuth:PublicKey", ecdsa.ExportSubjectPublicKeyInfoPem());
            b.UseSetting("Scheduling:Enabled", "false");
            b.UseSetting("Payments:Mode", "Production");
            b.UseSetting("Payments:AllowMockEmail", "true"); // the unsafe bit → boot refusal
        });

        var ex = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(ex);
        Assert.Contains("AllowMockEmail", Flatten(ex));
    }

    [Fact]
    public void Production_host_with_LocalMock_mode_refuses_to_boot()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var factory = new WebApplicationFactory<ThreeCommerce.Payments.Api.IApiMarker>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(Environments.Production);
            b.UseSetting("InternalAuth:PublicKey", ecdsa.ExportSubjectPublicKeyInfoPem());
            b.UseSetting("Scheduling:Enabled", "false");
            b.UseSetting("Payments:Mode", "LocalMock"); // the mock path itself → boot refusal
        });

        var ex = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(ex);
        Assert.Contains("LocalMock", Flatten(ex));
    }

    private static string Flatten(Exception ex)
    {
        var messages = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
            {
                messages.AddRange(aggregate.InnerExceptions.Select(i => i.Message));
            }
        }

        return string.Join(" | ", messages);
    }

    private static async Task<EmailMessage> WaitForEmailAsync(RecordingEmailSender recorder, Guid orderId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var match = recorder.Sent.FirstOrDefault(m => m.Body.Contains(orderId.ToString()));
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"No TEST-ONLY mock payment email arrived for order {orderId}.");
    }

    private async Task SimulatePaymentAsync(Guid orderId, long gross)
    {
        await WaitForSagaAsync(orderId);
        using var payments = fixture.Payments.CreateClient();
        var intentId = $"pi_fake_{orderId:N}";
        (await payments.PostAsync($"/dev/simulate-payment/{intentId}?amountMinor={gross}", null)).EnsureSuccessStatusCode();
    }

    private async Task WaitForSagaAsync(Guid orderId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ThreeCommerce.Ordering.Infrastructure.OrderingDbContext>();
            if (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                    db.CheckoutStates.Where(s => s.CorrelationId == orderId)))
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Checkout saga for {orderId} did not start.");
    }

    private static async Task WaitForStatusAsync(HttpClient client, Guid orderId, string expected)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<StatusDto>($"/orders/{orderId}/status");
            if (status?.Status == expected)
            {
                return;
            }

            await Task.Delay(300);
        }

        throw new TimeoutException($"Order {orderId} did not reach {expected}.");
    }
}
