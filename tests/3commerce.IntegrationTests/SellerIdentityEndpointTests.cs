using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Entity;
using ThreeCommerce.BuildingBlocks.Contracts.Tax;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Entity.Infrastructure;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0063 P2 through the real Entity API: per-scheme tax-ID validation returns 400 problem+json naming the
/// field; the seller of record + its establishments; and every admin mutation records a local audit entry AND
/// delivers AuditEntryRecorded + LegalEntityChanged from the same unit of work (bus outbox → broker).
/// Each test runs under its own tenant (the internal-claims tenant drives the request's RLS scope).
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class SellerIdentityEndpointTests(Phase2Fixture fixture) : IAsyncLifetime
{
    private const int Company = 2; // EntityType.Company — enums cross HTTP as numbers
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ThreeCommerce.Entity.Api.IApiMarker> _entity = null!;
    private HttpClient _admin = null!;

    public Task InitializeAsync()
    {
        _entity = fixture.CreateEntityFactory();
        _admin = _entity.CreateClient();
        _admin.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), Roles.Admin, tenantId: _tenantId.ToString()));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _entity.Dispose();
        return Task.CompletedTask;
    }

    private sealed record IdentifierDto(Guid Id, int Type, string Value, int VerificationStatus, string? Jurisdiction);
    private sealed record EstablishmentDto(Guid Id, string Key, string Label, string City, string CountryCode, string Jurisdiction);
    private sealed record DetailDto(Guid Id, Guid TenantId, string LegalName, List<IdentifierDto> Identifiers, bool IsSeller, List<EstablishmentDto>? Establishments);
    private sealed record SellerDto(Guid EntityId, string LegalName, List<IdentifierDto> Identifiers, List<EstablishmentDto> Establishments);

    [Theory]
    [InlineData(5, "DE12345678", null, "Value")] // EuVat: DE needs 9 digits
    [InlineData(5, "DE123456789", "FR", "Jurisdiction")] // EuVat: prefix vs jurisdiction
    [InlineData(6, "GB1234", null, "Value")] // GbVrn
    [InlineData(7, "12", null, "Value")] // NzGst
    [InlineData(8, "1234", null, "Value")] // CaBn
    [InlineData(9, "CHE123", null, "Value")] // ChUid
    [InlineData(10, "12345", null, "Jurisdiction")] // UsStateSalesTaxId without its state
    public async Task Invalid_tax_id_is_a_400_problem_naming_the_field(int scheme, string value, string? jurisdiction, string field)
    {
        var entityId = await CreateEntityAsync("Validation Co");

        var response = await _admin.PostAsJsonAsync($"/entities/{entityId}/identifiers", new { type = scheme, value, jurisdiction });

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var errors = JsonDocument.Parse(raw).RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty(field, out var messages), $"errors does not name '{field}': {raw}");
        Assert.NotEmpty(messages.EnumerateArray());
        Assert.Empty((await DetailAsync(entityId)).Identifiers); // nothing was stored
    }

    [Theory]
    [InlineData(5, "de 123 456 789", null, "DE123456789", "DE")]
    [InlineData(6, "GB123456789", null, "GB123456789", "GB")]
    [InlineData(7, "123-456-789", null, "123456789", "NZ")]
    [InlineData(8, "123456789RT0001", null, "123456789RT0001", "CA")]
    [InlineData(9, "CHE-123.456.789 MWST", null, "CHE123456789MWST", "CH")]
    [InlineData(10, "SR-12345", "US-CA", "SR-12345", "US-CA")]
    public async Task Valid_tax_id_is_stored_normalized_with_its_jurisdiction(int scheme, string value, string? jurisdiction, string stored, string storedJurisdiction)
    {
        // Positive control for the 400 cases above: the same endpoint accepts a well-formed ID of each scheme.
        var entityId = await CreateEntityAsync("Valid Co");

        var response = await _admin.PostAsJsonAsync($"/entities/{entityId}/identifiers", new { type = scheme, value, jurisdiction });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var identifier = Assert.Single((await response.Content.ReadFromJsonAsync<DetailDto>())!.Identifiers);
        Assert.Equal((scheme, stored, storedJurisdiction), (identifier.Type, identifier.Value, identifier.Jurisdiction));
    }

    [Fact]
    public async Task Seller_identity_mutations_audit_and_publish_LegalEntityChanged_in_the_same_unit_of_work()
    {
        var changes = new ConcurrentQueue<LegalEntityChanged>();
        var audited = new ConcurrentQueue<AuditEntryRecorded>();
        var probe = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host(new Uri(fixture.RabbitMqUri));
            cfg.ReceiveEndpoint($"seller-identity-probe-{Guid.NewGuid():N}", e =>
            {
                e.AutoDelete = true;
                e.Durable = false;
                e.Handler<LegalEntityChanged>(ctx =>
                {
                    if (ctx.Message.TenantId == _tenantId)
                    {
                        changes.Enqueue(ctx.Message);
                    }

                    return Task.CompletedTask;
                });
                e.Handler<AuditEntryRecorded>(ctx =>
                {
                    if (ctx.Message.TenantId == _tenantId)
                    {
                        audited.Enqueue(ctx.Message);
                    }

                    return Task.CompletedTask;
                });
            });
        });
        await probe.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        try
        {
            var sellerId = await CreateEntityAsync("Seller Pty Ltd");
            var otherId = await CreateEntityAsync("Other Pty Ltd");

            // Not yet the seller: no establishments (409), and a tax ID on a non-seller publishes nothing.
            var early = await _admin.PostAsJsonAsync($"/entities/{sellerId}/establishments", Establishment("sydney-hq", "AU", null));
            Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
            Assert.Equal("application/problem+json", early.Content.Headers.ContentType?.MediaType);
            Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync($"/entities/{otherId}/identifiers", new { type = 5, value = "FRAB123456789" })).StatusCode);

            // Designate → seller; a second designation in the same tenant is refused.
            var designate = await _admin.PostAsync($"/entities/{sellerId}/seller", null);
            Assert.Equal(HttpStatusCode.OK, designate.StatusCode);
            Assert.True((await designate.Content.ReadFromJsonAsync<DetailDto>())!.IsSeller);
            var second = await _admin.PostAsync($"/entities/{otherId}/seller", null);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

            // Tax ID + establishment (+ an invalid one naming its field) + an edit.
            Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync($"/entities/{sellerId}/identifiers", new { type = 1, value = "51 824 753 556" })).StatusCode);
            var bad = await _admin.PostAsJsonAsync($"/entities/{sellerId}/establishments", Establishment("melbourne", "AU", "NZ"));
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.True(JsonDocument.Parse(await bad.Content.ReadAsStringAsync()).RootElement.GetProperty("errors").TryGetProperty("Jurisdiction", out _));
            var added = await _admin.PostAsJsonAsync($"/entities/{sellerId}/establishments", Establishment("sydney-hq", "AU", null));
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            var establishment = Assert.Single((await added.Content.ReadFromJsonAsync<DetailDto>())!.Establishments!);
            Assert.Equal(("sydney-hq", "AU"), (establishment.Key, establishment.Jurisdiction));
            var duplicateKey = await _admin.PostAsJsonAsync($"/entities/{sellerId}/establishments", Establishment("sydney-hq", "AU", null));
            Assert.Equal(HttpStatusCode.BadRequest, duplicateKey.StatusCode);
            var update = await _admin.PutAsJsonAsync($"/entities/{sellerId}/establishments/{establishment.Id}", new
            {
                label = "Sydney office", line1 = "2 George St", city = "Sydney", region = "NSW", postcode = "2000", countryCode = "AU",
            });
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);

            // GET /entities/seller reflects it all.
            var seller = (await _admin.GetFromJsonAsync<SellerDto>("/entities/seller"))!;
            Assert.Equal(sellerId, seller.EntityId);
            Assert.Equal("Sydney office", Assert.Single(seller.Establishments).Label);
            Assert.Equal(("51824753556", "AU"), (Assert.Single(seller.Identifiers).Value, seller.Identifiers[0].Jurisdiction));

            // Audit: one local entry per accepted seller mutation, committed with it...
            string[] actions = ["entity.seller.designate", "entity.identifier.add", "entity.establishment.add", "entity.establishment.update"];
            using (var scope = _entity.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<EntityDbContext>();
                var local = await db.RunInTenantScopeAsync(TenantContext.ForTenant(_tenantId),
                    () => db.AuditEntries.AsNoTracking().Where(a => a.ResourceId == sellerId.ToString()).Select(a => a.Action).ToListAsync());
                foreach (var action in actions)
                {
                    Assert.Single(local, a => a == action);
                }

                // ...and refused mutations (409 / 400) recorded nothing.
                Assert.Equal(actions.Length + 1, local.Count); // + entity.entity.create
            }

            // ...and each one reached the broker as AuditEntryRecorded, beside a LegalEntityChanged snapshot.
            foreach (var action in actions)
            {
                await WaitForAsync(audited, a => a.Action == action && a.ResourceId == sellerId.ToString(), $"AuditEntryRecorded {action}");
            }

            var final = await WaitForAsync(changes, c => c.Establishments.Any(e => e.Label == "Sydney office"), "the post-update LegalEntityChanged");
            Assert.Equal(sellerId, final.EntityId);
            Assert.True(final.IsSeller);
            Assert.Equal(TaxIdScheme.Abn, Assert.Single(final.TaxIdentifiers).Scheme);
            Assert.Equal("2 George St", Assert.Single(final.Establishments).Address.Line1);
            // Negative control: the non-seller's tax ID never produced a LegalEntityChanged.
            await Task.Delay(500);
            Assert.DoesNotContain(changes, c => c.EntityId == otherId);
            Assert.Equal(4, changes.Count(c => c.EntityId == sellerId)); // designate, identifier, add, update
        }
        finally
        {
            await probe.StopAsync();
        }
    }

    private static object Establishment(string key, string country, string? jurisdiction) => new
    {
        key,
        label = "Sydney HQ",
        line1 = "1 George St",
        city = "Sydney",
        region = "NSW",
        postcode = "2000",
        countryCode = country,
        jurisdiction,
    };

    private async Task<Guid> CreateEntityAsync(string legalName)
    {
        var response = await _admin.PostAsJsonAsync("/entities", new { tenantId = _tenantId, type = Company, legalName = $"{legalName} {Guid.NewGuid():N}"[..40] });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DetailDto>())!.Id;
    }

    private async Task<DetailDto> DetailAsync(Guid entityId) => (await _admin.GetFromJsonAsync<DetailDto>($"/entities/{entityId}"))!;

    private static async Task<T> WaitForAsync<T>(ConcurrentQueue<T> queue, Func<T, bool> match, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var hit = queue.FirstOrDefault(match);
            if (hit is not null)
            {
                return hit;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }
}
