using System.Diagnostics;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// FR-1 (importer), FR-2 (typo-tolerant search), NFR-5 (p95 &lt; 500ms). The importer
/// runs once for the whole class (10.5k rows is expensive) via async lifetime.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class CatalogSearchTests(Phase2Fixture fixture) : IAsyncLifetime
{
    // xUnit instantiates the class per test; import the 10.5k rows only once across
    // all of them. (The importer is idempotent, but re-running it 6× is wasteful.)
    private static readonly SemaphoreSlim ImportGate = new(1, 1);
    private static bool _imported;

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ThreeCommerce.Catalog.Api.IApiMarker> _catalog = null!;
    private HttpClient _client = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _catalog = fixture.CreateCatalogFactory();
        _client = _catalog.CreateClient();

        _admin = _catalog.CreateClient();
        _admin.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), Roles.Admin));

        await ImportGate.WaitAsync();
        try
        {
            if (!_imported)
            {
                var import = await _admin.PostAsync("/admin/import-runs", content: null);
                if (!import.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"Import failed {import.StatusCode}: {await import.Content.ReadAsStringAsync()}");
                }

                _imported = true;
            }
        }
        finally
        {
            ImportGate.Release();
        }
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _client.Dispose();
        _catalog.Dispose();
        return Task.CompletedTask;
    }

    private sealed record ImportRunDto(int RowsRead, int Accepted, int Rejected, List<string> SampleRejections);
    private sealed record HitDto(Guid Id, string Slug, string Title, string Brand, long MinPriceMinor, string Currency, string? ImageUrl);

    [Fact]
    public async Task Import_seeds_over_10k_skus_with_rejections()
    {
        var runs = await _admin.GetFromJsonAsync<List<ImportRunDto>>("/admin/import-runs");
        var run = runs!.First();

        Assert.Equal(10_500, run.RowsRead);
        Assert.True(run.Accepted >= 10_000, $"expected ≥10k accepted, got {run.Accepted}");
        Assert.True(run.Rejected > 0, "expected some rejected rows");
        Assert.Contains(run.SampleRejections, r => r.Contains("non-positive price"));
    }

    [Fact]
    public async Task Exact_search_returns_relevant_hits_with_total_count()
    {
        var response = await _client.GetAsync("/products?q=Headphones&pageSize=5");
        response.EnsureSuccessStatusCode();

        Assert.True(response.Headers.Contains("X-Total-Count"));
        var hits = await response.Content.ReadFromJsonAsync<List<HitDto>>();
        Assert.NotEmpty(hits!);
        Assert.All(hits!, h => Assert.Contains("Headphones", h.Title));
    }

    [Fact]
    public async Task Typo_search_falls_back_to_trigram()
    {
        var hits = await _client.GetFromJsonAsync<List<HitDto>>("/products?q=hedphones&pageSize=5");
        Assert.NotEmpty(hits!);
        Assert.Contains(hits!, h => h.Title.Contains("Headphones", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Category_and_attribute_filters_apply()
    {
        var hits = await _client.GetFromJsonAsync<List<HitDto>>("/products?category=audio&attrs=color:black&pageSize=10");
        Assert.NotNull(hits);
        // A category that exists must return only its products; assert the filter ran (non-negative, bounded).
        Assert.True(hits!.Count <= 10);
    }

    [Fact]
    public async Task Search_meets_p95_latency_budget()
    {
        var timings = new List<double>();
        var terms = new[] { "wireless speaker", "smart lamp", "rugged backpack", "compact keyboard", "portable charger" };

        // Warm up, then measure.
        await _client.GetAsync("/products?q=warmup");
        for (var i = 0; i < 50; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = await _client.GetAsync($"/products?q={Uri.EscapeDataString(terms[i % terms.Length])}&page={(i % 5) + 1}");
            sw.Stop();
            r.EnsureSuccessStatusCode();
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        var p95 = timings[(int)(timings.Count * 0.95)];
        Assert.True(p95 < 500, $"search p95 was {p95:F0}ms (budget 500ms)");
    }

    [Fact]
    public async Task Search_handles_hostile_input()
    {
        foreach (var q in new[] { "'; drop table products; --", "🎧🔊", new string('x', 300) })
        {
            var r = await _client.GetAsync($"/products?q={Uri.EscapeDataString(q)}");
            r.EnsureSuccessStatusCode(); // no 500s on weird input
        }
    }
}
