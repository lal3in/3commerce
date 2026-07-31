using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Product ratings &amp; reviews: everyone reads; only a signed-in, EMAIL-VERIFIED customer may
/// write (one per product, re-posting updates it); only an admin may remove one.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class ProductReviewTests(Phase2Fixture fixture) : IAsyncLifetime
{
    private WebApplicationFactory<ThreeCommerce.Catalog.Api.IApiMarker> _catalog = null!;
    private HttpClient _public = null!;
    private Guid _productId;

    private sealed record ReviewDto(Guid Id, string AuthorName, int Rating, string? Comment, DateTimeOffset CreatedAt);
    private sealed record SummaryDto(Guid ProductId, double Average, int Count, List<ReviewDto> Items);
    private sealed record AdminReviewDto(Guid Id, Guid ProductId, string ProductName, string ProductSlug, string AuthorName, int Rating, string? Comment, DateTimeOffset CreatedAt);

    public async Task InitializeAsync()
    {
        _catalog = fixture.CreateCatalogFactory();
        _public = _catalog.CreateClient();

        using var scope = _catalog.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        _productId = Guid.CreateVersion7();
        db.Products.Add(new Product
        {
            Id = _productId,
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            CategoryId = Guid.CreateVersion7(),
            Slug = $"review-prod-{_productId:N}",
            Title = "Reviewable Widget",
            Brand = "Acme",
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _public.Dispose();
        _catalog.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient Client(string role, bool emailVerified) =>
        WithClaims(_catalog.CreateClient(), fixture.MintInternalClaims(Guid.CreateVersion7(), role, "shopper@example.test", emailVerified: emailVerified));

    private HttpClient ClientForUser(Guid userId, bool emailVerified) =>
        WithClaims(_catalog.CreateClient(), fixture.MintInternalClaims(userId, "customer", "shopper@example.test", emailVerified: emailVerified));

    private static HttpClient WithClaims(HttpClient c, string token)
    {
        c.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, token);
        return c;
    }

    [Fact]
    public async Task Everyone_reads_verified_customer_writes_admin_removes()
    {
        // Public read: empty to start.
        var empty = await _public.GetFromJsonAsync<SummaryDto>($"/products/{_productId}/reviews");
        Assert.Equal(0, empty!.Count);

        // Verified customer writes.
        var verified = Client("customer", emailVerified: true);
        var post = await verified.PostAsJsonAsync($"/products/{_productId}/reviews", new { rating = 5, comment = "Great!", authorName = "Jane" });
        post.EnsureSuccessStatusCode();

        // Public read now shows it (aggregate + item), to anyone.
        var summary = await _public.GetFromJsonAsync<SummaryDto>($"/products/{_productId}/reviews");
        Assert.Equal(1, summary!.Count);
        Assert.Equal(5, summary.Average);
        Assert.Contains(summary.Items, r => r.Rating == 5 && r.Comment == "Great!" && r.AuthorName == "Jane");

        // Admin removes it (moderation).
        var admin = Client("admin", emailVerified: true);
        var list = await admin.GetFromJsonAsync<List<AdminReviewDto>>("/admin/reviews");
        var id = list!.Single(r => r.ProductId == _productId).Id;
        var del = await admin.DeleteAsync($"/admin/reviews/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var afterDelete = await _public.GetFromJsonAsync<SummaryDto>($"/products/{_productId}/reviews");
        Assert.Equal(0, afterDelete!.Count);
    }

    [Fact]
    public async Task Unverified_customer_cannot_write()
    {
        var unverified = Client("customer", emailVerified: false);
        var post = await unverified.PostAsJsonAsync($"/products/{_productId}/reviews", new { rating = 4, comment = "nope" });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public async Task Anonymous_cannot_write()
    {
        var post = await _public.PostAsJsonAsync($"/products/{_productId}/reviews", new { rating = 4, comment = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
    }

    [Fact]
    public async Task Re_posting_updates_the_same_review()
    {
        var userId = Guid.CreateVersion7();
        var first = await ClientForUser(userId, true).PostAsJsonAsync($"/products/{_productId}/reviews", new { rating = 2, comment = "meh" });
        first.EnsureSuccessStatusCode();
        var second = await ClientForUser(userId, true).PostAsJsonAsync($"/products/{_productId}/reviews", new { rating = 5, comment = "grew on me" });
        second.EnsureSuccessStatusCode();

        var summary = await _public.GetFromJsonAsync<SummaryDto>($"/products/{_productId}/reviews");
        Assert.Equal(1, summary!.Count); // upsert, not a duplicate
        Assert.Equal(5, summary.Average);
    }
}
