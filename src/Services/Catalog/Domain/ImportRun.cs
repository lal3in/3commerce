namespace ThreeCommerce.Catalog.Domain;

/// <summary>Persisted per importer execution — feeds the admin monitoring dashboard (FR-1).</summary>
public class ImportRun
{
    public Guid Id { get; init; }
    public required string Importer { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int RowsRead { get; set; }
    public int Accepted { get; set; }
    public int Rejected { get; set; }
    /// <summary>First N rejection reasons, for diagnosis without log digging.</summary>
    public List<string> SampleRejections { get; set; } = [];
}
