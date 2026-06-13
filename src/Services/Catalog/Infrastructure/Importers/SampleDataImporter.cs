using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Infrastructure.Importers;

/// <summary>
/// Deterministic sample-data importer (ADR-0004): seeded RNG, ≥10k SKUs, ~0.4%
/// deliberately invalid rows to exercise rejection paths. Rerun-idempotent —
/// upserts by slug, so a second run updates instead of duplicating.
/// </summary>
public sealed class SampleDataImporter(
    CatalogDbContext db,
    IPublishEndpoint publisher,
    ILogger<SampleDataImporter> logger) : ISupplierImporter
{
    public const int TargetRows = 10_500;
    private const int BatchSize = 500;
    private const int Seed = 42;

    private static readonly string[] CategoryNames =
        ["Audio", "Computing", "Phones", "Gaming", "Home", "Kitchen", "Fitness", "Outdoor", "Toys", "Office", "Lighting", "Wearables"];
    private static readonly string[] Brands =
        ["Acmetек", "Borealis", "Cryon", "Dektra", "Elumen", "Fjordic", "Gravix", "Helix", "Ionware", "Juno", "Kantor", "Lumina", "Mistral", "Nexa", "Orbis", "Pylon", "Quanta", "Rivet", "Solace", "Tundra"];
    private static readonly string[] Adjectives =
        ["Wireless", "Compact", "Ergonomic", "Portable", "Smart", "Ultra", "Pro", "Classic", "Foldable", "Rugged", "Silent", "Rapid"];
    private static readonly string[] Nouns =
        ["Headphones", "Speaker", "Keyboard", "Mouse", "Monitor", "Charger", "Lamp", "Bottle", "Backpack", "Tripod", "Blender", "Kettle", "Mat", "Band", "Hub", "Stand", "Case", "Cable"];
    private static readonly string[] Colors = ["black", "white", "silver", "blue", "red", "green"];
    private static readonly string[] Sizes = ["s", "m", "l"];
    private static readonly string[] Materials = ["aluminium", "plastic", "steel", "fabric", "bamboo"];

    public string Name => "sample-data";

    public async Task<ImportRunResult> RunAsync(CancellationToken ct)
    {
        var run = new ImportRun
        {
            Id = Guid.CreateVersion7(),
            Importer = Name,
            StartedAt = DateTimeOffset.UtcNow,
        };
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var categories = await EnsureCategoriesAsync(ct);
        var existingBySlug = await db.Products.Include(p => p.Variants)
            .ToDictionaryAsync(p => p.Slug, ct);

        int read = 0, accepted = 0, rejected = 0, pendingInBatch = 0;
        var rejections = new List<string>();

        for (var i = 0; i < TargetRows; i++)
        {
            ct.ThrowIfCancellationRequested();
            read++;

            // Per-row seeded RNG: row i is fully deterministic and independent of any
            // other row, so slugs are stable and re-imports upsert idempotently.
            var rng = new Random(unchecked(Seed + i));

            var brand = Brands[rng.Next(Brands.Length)];
            var title = $"{brand} {Adjectives[rng.Next(Adjectives.Length)]} {Nouns[rng.Next(Nouns.Length)]} {i % 97}";
            var basePrice = 500L + rng.Next(1, 4000) * 25L;
            var variantCount = 1 + rng.Next(3);

            // Deliberate bad rows: every 250th has a non-positive price; every 251st an empty title.
            if (i % 250 == 249)
            {
                basePrice = 0;
            }

            if (i % 251 == 250)
            {
                title = "  ";
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                rejected++;
                Reject(rejections, $"row {i}: empty title");
                continue;
            }

            if (basePrice <= 0)
            {
                rejected++;
                Reject(rejections, $"row {i}: non-positive price");
                continue;
            }

            var slug = $"p-{i:d5}-{Slugify(title)}";
            var category = categories[rng.Next(categories.Count)];
            var attributes = new Dictionary<string, string>
            {
                ["color"] = Colors[rng.Next(Colors.Length)],
                ["size"] = Sizes[rng.Next(Sizes.Length)],
                ["material"] = Materials[rng.Next(Materials.Length)],
            };
            var images = new List<string> { $"https://picsum.photos/seed/{slug}/600/600" };
            var description =
                $"The {title} by {brand} combines {attributes["material"]} construction with a {attributes["color"]} finish. " +
                $"Designed for everyday use, available in size {attributes["size"].ToUpperInvariant()}.";

            if (existingBySlug.TryGetValue(slug, out var existing))
            {
                existing.Title = title;
                existing.Brand = brand;
                existing.Description = description;
                existing.CategoryId = category.Id;
                existing.Attributes = attributes;
                existing.ImageUrls = images;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                for (var v = 0; v < existing.Variants.Count; v++)
                {
                    existing.Variants[v].PriceMinor = basePrice + v * 500;
                }

                await PublishUpsertedAsync(existing, ct);
            }
            else
            {
                var product = new Product
                {
                    Id = Guid.CreateVersion7(),
                    Slug = slug,
                    Title = title,
                    Brand = brand,
                    Description = description,
                    CategoryId = category.Id,
                    Attributes = attributes,
                    ImageUrls = images,
                    SupplierRef = $"sample:{i}",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                for (var v = 0; v < variantCount; v++)
                {
                    product.Variants.Add(new Variant
                    {
                        Id = Guid.CreateVersion7(),
                        ProductId = product.Id,
                        Sku = $"SKU-{i:d5}-{v}",
                        PriceMinor = basePrice + v * 500,
                        Currency = "EUR",
                        StockQuantity = rng.Next(0, 200),
                    });
                }

                db.Products.Add(product);
                await PublishUpsertedAsync(product, ct);
            }

            accepted++;
            if (++pendingInBatch >= BatchSize)
            {
                await db.SaveChangesAsync(ct);
                pendingInBatch = 0;
            }
        }

        run.RowsRead = read;
        run.Accepted = accepted;
        run.Rejected = rejected;
        run.SampleRejections = rejections;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Import {Run}: read={Read} accepted={Accepted} rejected={Rejected}",
            run.Id, read, accepted, rejected);

        return new ImportRunResult(run.Id, read, accepted, rejected, rejections);
    }

    private Task PublishUpsertedAsync(Product product, CancellationToken ct) =>
        publisher.Publish(new ProductUpserted(
            product.Id,
            product.Slug,
            product.Title,
            product.Variants.Count > 0 ? product.Variants.Min(v => v.PriceMinor) : 0,
            product.Variants.FirstOrDefault()?.Currency ?? "EUR",
            product.ImageUrls.FirstOrDefault()), ct);

    private async Task<List<Category>> EnsureCategoriesAsync(CancellationToken ct)
    {
        var existing = await db.Categories.ToListAsync(ct);
        if (existing.Count > 0)
        {
            return existing;
        }

        var categories = CategoryNames.Select(name => new Category
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Slug = Slugify(name),
        }).ToList();
        db.Categories.AddRange(categories);
        await db.SaveChangesAsync(ct);
        return categories;
    }

    private static void Reject(List<string> rejections, string reason)
    {
        if (rejections.Count < 50)
        {
            rejections.Add(reason);
        }
    }

    private static string Slugify(string value) =>
        new(value.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray());
}
