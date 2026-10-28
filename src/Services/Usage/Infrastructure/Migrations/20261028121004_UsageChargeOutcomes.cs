using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Usage.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UsageChargeOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PaymentHold",
                schema: "usage",
                table: "UsageBalances",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.CreateTable(
                name: "UsageCharges",
                schema: "usage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    BalanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Kind = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Quantity = table.Column<long>(type: "bigint", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    State = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    LastFailureReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    NextRetryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageCharges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsageCharges_BalanceId_State",
                schema: "usage",
                table: "UsageCharges",
                columns: new[] { "BalanceId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageCharges_TenantId_Reference",
                schema: "usage",
                table: "UsageCharges",
                columns: new[] { "TenantId", "Reference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UsageCharges",
                schema: "usage");

            migrationBuilder.DropColumn(
                name: "PaymentHold",
                schema: "usage",
                table: "UsageBalances");
        }
    }
}
