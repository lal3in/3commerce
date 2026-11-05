using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PerLineTaxDestination : Migration
    {
        /// <inheritdoc />
        // ADR-0063 § 16 per-line destination: each line records where it was supplied for tax ('' on existing rows =
        // the order's destination, no backfill — D18), and a sale split across destinations records one
        // EuDistanceSales row per destination, so the key becomes (OrderId, DestinationCountry). Every existing row is
        // one per order, so the new key is satisfied as is. Down fails once a split sale has recorded two rows.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_EuDistanceSales",
                schema: "ordering",
                table: "EuDistanceSales");

            migrationBuilder.AddColumn<string>(
                name: "TaxDestinationCountry",
                schema: "ordering",
                table: "OrderLines",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TaxDestinationCountry",
                schema: "ordering",
                table: "CheckoutAttemptLines",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_EuDistanceSales",
                schema: "ordering",
                table: "EuDistanceSales",
                columns: new[] { "OrderId", "DestinationCountry" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_EuDistanceSales",
                schema: "ordering",
                table: "EuDistanceSales");

            migrationBuilder.DropColumn(
                name: "TaxDestinationCountry",
                schema: "ordering",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "TaxDestinationCountry",
                schema: "ordering",
                table: "CheckoutAttemptLines");

            migrationBuilder.AddPrimaryKey(
                name: "PK_EuDistanceSales",
                schema: "ordering",
                table: "EuDistanceSales",
                column: "OrderId");
        }
    }
}
