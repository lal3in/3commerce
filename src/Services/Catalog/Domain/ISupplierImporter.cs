namespace ThreeCommerce.Catalog.Domain;

/// <summary>
/// The supplier seam (ADR-0004): real feeds (API/CSV/EDI) plug in as new
/// implementations without touching the catalog core.
/// </summary>
public interface ISupplierImporter
{
    public string Name { get; }

    public Task<ImportRunResult> RunAsync(CancellationToken ct);
}

public record ImportRunResult(Guid RunId, int RowsRead, int Accepted, int Rejected, IReadOnlyList<string> SampleRejections);
