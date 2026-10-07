using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <summary>
/// Splits the StorefrontServiceReadiness read model into one table per go-live signal, each written by its own
/// consumer with an atomic upsert, and replaces the old table with a view of the same name and shape so readers
/// are unchanged. Two consumers first-inserting one shared row raced (23505 on PK_StorefrontServiceReadiness);
/// under the outbox's REPEATABLE READ transaction no upsert of a shared row can avoid that, separate rows can.
/// Existing rows are copied into both tables, so the view returns exactly what the table held.
/// </summary>
public partial class SplitStorefrontServiceReadiness : Migration
{
    private const string CreateView = """
        CREATE VIEW catalog."StorefrontServiceReadiness" AS
        SELECT COALESCE(c."StorefrontId", p."StorefrontId") AS "StorefrontId",
               COALESCE(c."TenantId", p."TenantId") AS "TenantId",
               COALESCE(c."HasActiveCarrier", false) AS "HasActiveCarrier",
               COALESCE(p."HasActivePaymentAccount", false) AS "HasActivePaymentAccount"
        FROM catalog."StorefrontCarrierReadiness" c
        FULL OUTER JOIN catalog."StorefrontPaymentReadiness" p ON p."StorefrontId" = c."StorefrontId";
        """;

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "StorefrontCarrierReadiness",
            schema: "catalog",
            columns: table => new
            {
                StorefrontId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                HasActiveCarrier = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StorefrontCarrierReadiness", x => x.StorefrontId);
            });

        migrationBuilder.CreateTable(
            name: "StorefrontPaymentReadiness",
            schema: "catalog",
            columns: table => new
            {
                StorefrontId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                HasActivePaymentAccount = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StorefrontPaymentReadiness", x => x.StorefrontId);
            });

        migrationBuilder.Sql("""
            INSERT INTO catalog."StorefrontCarrierReadiness" ("StorefrontId", "TenantId", "HasActiveCarrier")
            SELECT "StorefrontId", "TenantId", "HasActiveCarrier" FROM catalog."StorefrontServiceReadiness";
            INSERT INTO catalog."StorefrontPaymentReadiness" ("StorefrontId", "TenantId", "HasActivePaymentAccount")
            SELECT "StorefrontId", "TenantId", "HasActivePaymentAccount" FROM catalog."StorefrontServiceReadiness";
            """);

        migrationBuilder.DropTable(
            name: "StorefrontServiceReadiness",
            schema: "catalog");

        migrationBuilder.Sql(CreateView);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""ALTER VIEW catalog."StorefrontServiceReadiness" RENAME TO "StorefrontServiceReadiness_split";""");

        migrationBuilder.CreateTable(
            name: "StorefrontServiceReadiness",
            schema: "catalog",
            columns: table => new
            {
                StorefrontId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                HasActiveCarrier = table.Column<bool>(type: "boolean", nullable: false),
                HasActivePaymentAccount = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StorefrontServiceReadiness", x => x.StorefrontId);
            });

        migrationBuilder.Sql("""
            INSERT INTO catalog."StorefrontServiceReadiness" ("StorefrontId", "TenantId", "HasActiveCarrier", "HasActivePaymentAccount")
            SELECT "StorefrontId", "TenantId", "HasActiveCarrier", "HasActivePaymentAccount"
            FROM catalog."StorefrontServiceReadiness_split";
            DROP VIEW catalog."StorefrontServiceReadiness_split";
            """);

        migrationBuilder.DropTable(
            name: "StorefrontCarrierReadiness",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "StorefrontPaymentReadiness",
            schema: "catalog");
    }
}
