using Microsoft.EntityFrameworkCore;
using Npgsql;
using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Infrastructure.Search;

/// <summary>
/// v1 of the ISearchProvider seam (ADR-0020): weighted tsvector FTS with a pg_trgm
/// similarity fallback when full-text yields nothing (typo tolerance).
/// </summary>
public sealed class PostgresSearchProvider(CatalogDbContext db) : ISearchProvider
{
    private sealed record HitRow(Guid Id, string Slug, string Title, string Brand, long MinPriceMinor, string Currency, string? ImageUrl);

    public async Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var (whereSql, parameters) = BuildFilters(query);

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var fts = await RunAsync(
                $"p.search_vector @@ websearch_to_tsquery('english', @q){whereSql}",
                "ts_rank(p.search_vector, websearch_to_tsquery('english', @q)) DESC",
                parameters.Append(new NpgsqlParameter("q", query.Text)).ToArray(),
                page, pageSize, ct);

            if (fts.TotalCount > 0)
            {
                return fts;
            }

            // Typo fallback: trigram similarity on title.
            return await RunAsync(
                $"similarity(p.\"Title\", @q) > 0.25{whereSql}",
                "similarity(p.\"Title\", @q) DESC",
                parameters.Append(new NpgsqlParameter("q", query.Text)).ToArray(),
                page, pageSize, ct);
        }

        return await RunAsync(
            $"TRUE{whereSql}",
            "p.\"CreatedAt\" DESC",
            parameters.ToArray(),
            page, pageSize, ct);
    }

    private (string WhereSql, IEnumerable<NpgsqlParameter> Parameters) BuildFilters(SearchQuery query)
    {
        var sql = string.Empty;
        var parameters = new List<NpgsqlParameter>();

        if (!string.IsNullOrWhiteSpace(query.CategorySlug))
        {
            sql += " AND c.\"Slug\" = @category";
            parameters.Add(new NpgsqlParameter("category", query.CategorySlug));
        }

        var i = 0;
        foreach (var (key, value) in query.AttributeFilters)
        {
            sql += $" AND p.\"Attributes\" ->> @attrKey{i} = @attrVal{i}";
            parameters.Add(new NpgsqlParameter($"attrKey{i}", key));
            parameters.Add(new NpgsqlParameter($"attrVal{i}", value));
            i++;
        }

        return (sql, parameters);
    }

    private async Task<SearchResult> RunAsync(
        string whereSql, string orderSql, NpgsqlParameter[] parameters, int page, int pageSize, CancellationToken ct)
    {
        const string fromSql = """
            FROM "Products" p
            JOIN "Categories" c ON c."Id" = p."CategoryId"
            """;

        var countSql = $"""SELECT count(*)::int AS "Value" {fromSql} WHERE {whereSql}""";
        var total = (await db.Database
            .SqlQueryRaw<int>(countSql, parameters.Select(Clone).ToArray())
            .ToListAsync(ct)).Single();

        if (total == 0)
        {
            return new SearchResult([], 0);
        }

        var hitsSql = $"""
            SELECT p."Id", p."Slug", p."Title", p."Brand",
                   (SELECT min(v."PriceMinor") FROM "Variants" v WHERE v."ProductId" = p."Id") AS "MinPriceMinor",
                   (SELECT min(v."Currency")  FROM "Variants" v WHERE v."ProductId" = p."Id") AS "Currency",
                   (p."ImageUrls" ->> 0) AS "ImageUrl"
            {fromSql}
            WHERE {whereSql}
            ORDER BY {orderSql}
            LIMIT {pageSize} OFFSET {(page - 1) * pageSize}
            """;

        var rows = await db.Database
            .SqlQueryRaw<HitRow>(hitsSql, parameters.Select(Clone).ToArray())
            .ToListAsync(ct);

        var hits = rows
            .Select(r => new ProductHit(r.Id, r.Slug, r.Title, r.Brand, r.MinPriceMinor, r.Currency ?? "EUR", r.ImageUrl))
            .ToList();

        return new SearchResult(hits, total);
    }

    private static NpgsqlParameter Clone(NpgsqlParameter parameter) =>
        new(parameter.ParameterName, parameter.Value);
}
