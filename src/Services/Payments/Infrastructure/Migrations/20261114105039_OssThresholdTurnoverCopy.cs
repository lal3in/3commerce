using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OssThresholdTurnoverCopy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TaxJurisdiction",
                schema: "payments",
                table: "StorefrontLedgerAccounts",
                type: "character varying(6)",
                maxLength: 6,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "EuDistanceSaleCopies",
                schema: "payments",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationCountry = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    SourceReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    HomeCountry = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    NetMinor = table.Column<long>(type: "bigint", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EuDistanceSaleCopies", x => new { x.OrderId, x.DestinationCountry, x.SourceReference });
                });

            migrationBuilder.CreateIndex(
                name: "IX_EuDistanceSaleCopies_TenantId_Year_Currency",
                schema: "payments",
                table: "EuDistanceSaleCopies",
                columns: new[] { "TenantId", "Year", "Currency" })
                .Annotation("Npgsql:IndexInclude", new[] { "NetMinor" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EuDistanceSaleCopies",
                schema: "payments");

            migrationBuilder.DropColumn(
                name: "TaxJurisdiction",
                schema: "payments",
                table: "StorefrontLedgerAccounts");
        }
    }
}
