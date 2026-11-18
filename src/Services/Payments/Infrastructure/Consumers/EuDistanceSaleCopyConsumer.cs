using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>
/// Keeps Payments' copy of the sellers' EU distance-sales running total (ADR-0063 § 16, OSS threshold crossing): one
/// <see cref="EuDistanceSaleCopy"/> per <see cref="EuDistanceSaleRecorded"/>, keyed like Ordering's row, so a redelivery
/// (or a re-sent row) is stored once and the copy's Σ is Ordering's. A renewal reads it to re-decide its scheme.
/// </summary>
public sealed class EuDistanceSaleCopyConsumer(PaymentsDbContext db) : IConsumer<EuDistanceSaleRecorded>
{
    public async Task Consume(ConsumeContext<EuDistanceSaleRecorded> context)
    {
        var m = context.Message;
        var ct = context.CancellationToken;
        var destination = m.DestinationCountry.Trim().ToUpperInvariant();
        var source = m.SourceReference ?? string.Empty;
        if (await db.EuDistanceSaleCopies.AnyAsync(
                s => s.OrderId == m.OrderId && s.DestinationCountry == destination && s.SourceReference == source, ct))
        {
            return;
        }

        db.EuDistanceSaleCopies.Add(new EuDistanceSaleCopy
        {
            OrderId = m.OrderId,
            DestinationCountry = destination,
            SourceReference = source,
            TenantId = m.TenantId,
            HomeCountry = m.HomeCountry.Trim().ToUpperInvariant(),
            Year = m.Year,
            Currency = m.Currency.Trim().ToUpperInvariant(),
            NetMinor = m.NetMinor,
            RecordedAt = StoredTime.Of(m.RecordedAt),
        });
        await db.SaveChangesAsync(ct);
    }
}
