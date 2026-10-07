using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;

namespace ThreeCommerce.Catalog.Tests;

/// <summary>
/// Pins the storage shape that keeps the go-live readiness consumers race-free: each signal has its own table
/// (written only by its consumer's upsert), and the combined StorefrontServiceReadiness is a read-only view.
/// If the two signals ever share a table again, concurrent first writes for one storefront collide (23505/40001).
/// No database: the model is built from the DbContext configuration alone.
/// </summary>
public class StorefrontReadinessModelTests
{
    private static IModel DesignTimeModel()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=unused;Database=unused")
            .Options;
        using var db = new CatalogDbContext(options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    [Theory]
    [InlineData(typeof(StorefrontCarrierReadiness), "StorefrontCarrierReadiness", "HasActiveCarrier", "HasActivePaymentAccount")]
    [InlineData(typeof(StorefrontPaymentReadiness), "StorefrontPaymentReadiness", "HasActivePaymentAccount", "HasActiveCarrier")]
    public void Each_signal_has_its_own_table_keyed_by_storefront(Type entity, string table, string ownColumn, string otherColumn)
    {
        var type = DesignTimeModel().FindEntityType(entity)!;

        // The consumers' raw upsert SQL hardcodes catalog."<table>" and ON CONFLICT ("StorefrontId").
        Assert.Equal(table, type.GetTableName());
        Assert.Equal("catalog", type.GetSchema());
        Assert.Equal(["StorefrontId"], type.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.NotNull(type.FindProperty(ownColumn));
        Assert.Null(type.FindProperty(otherColumn));
    }

    [Fact]
    public void Combined_readiness_is_a_view_not_a_shared_table()
    {
        var type = DesignTimeModel().FindEntityType(typeof(StorefrontServiceReadiness))!;

        Assert.Null(type.GetTableName());
        Assert.Equal("StorefrontServiceReadiness", type.GetViewName());
        Assert.Equal("catalog", type.GetViewSchema());
    }
}
