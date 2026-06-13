namespace ThreeCommerce.Catalog.Domain;

/// <summary>
/// The search seam (ADR-0020): Postgres FTS in v1; a dedicated engine can slot in
/// later, fed by the ProductUpserted events already on the bus.
/// </summary>
public interface ISearchProvider
{
    public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct);
}

public record SearchQuery(
    string? Text,
    string? CategorySlug,
    IReadOnlyDictionary<string, string> AttributeFilters,
    int Page,
    int PageSize);

public record ProductHit(
    Guid Id,
    string Slug,
    string Title,
    string Brand,
    long MinPriceMinor,
    string Currency,
    string? ImageUrl);

public record SearchResult(IReadOnlyList<ProductHit> Hits, int TotalCount);
