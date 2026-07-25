using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class StorefrontLedgerAccounts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ReceivableAccountCode",
            schema: "catalog",
            table: "Storefronts",
            type: "character varying(80)",
            maxLength: 80,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "RevenueAccountCode",
            schema: "catalog",
            table: "Storefronts",
            type: "character varying(80)",
            maxLength: 80,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "TaxAccountCode",
            schema: "catalog",
            table: "Storefronts",
            type: "character varying(80)",
            maxLength: 80,
            nullable: false,
            defaultValue: "");

        // Backfill existing storefronts with the auto-derived defaults (matches Storefront
        // .DefaultAccountCode: "{kind}.store-{the id's full 32 hex chars}" — the full id, because a
        // short prefix of a UUIDv7 is the shared creation timestamp and collides across stores).
        migrationBuilder.Sql(@"
                UPDATE catalog.""Storefronts"" SET
                  ""ReceivableAccountCode"" = 'receivable.store-' || replace(""Id""::text, '-', ''),
                  ""RevenueAccountCode""    = 'revenue.store-'    || replace(""Id""::text, '-', ''),
                  ""TaxAccountCode""        = 'tax.store-'        || replace(""Id""::text, '-', '')
                WHERE ""ReceivableAccountCode"" = '';");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ReceivableAccountCode",
            schema: "catalog",
            table: "Storefronts");

        migrationBuilder.DropColumn(
            name: "RevenueAccountCode",
            schema: "catalog",
            table: "Storefronts");

        migrationBuilder.DropColumn(
            name: "TaxAccountCode",
            schema: "catalog",
            table: "Storefronts");
    }
}
