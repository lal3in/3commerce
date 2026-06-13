using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace ThreeCommerce.Fulfillment.Infrastructure;

public class FulfillmentDbContext(DbContextOptions<FulfillmentDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // MassTransit transactional outbox + inbox tables (ADR-0007).
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
