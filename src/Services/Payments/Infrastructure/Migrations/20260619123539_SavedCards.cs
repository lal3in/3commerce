using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SavedCards : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "ProviderCustomerId", schema: "payments", table: "Payments", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(name: "ProviderPaymentMethodId", schema: "payments", table: "Payments", type: "text", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "SavePaymentMethodRequested", schema: "payments", table: "Payments", type: "boolean", nullable: false, defaultValue: false);

        migrationBuilder.CreateTable(
            name: "PaymentCustomers",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ProviderCustomerId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_PaymentCustomers", x => x.Id));

        migrationBuilder.CreateTable(
            name: "SavedPaymentMethods",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PaymentCustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ProviderPaymentMethodId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Brand = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                ExpMonth = table.Column<int>(type: "integer", nullable: false),
                ExpYear = table.Column<int>(type: "integer", nullable: false),
                IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                State = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SavedPaymentMethods", x => x.Id);
                table.ForeignKey(
                    name: "FK_SavedPaymentMethods_PaymentCustomers_PaymentCustomerId",
                    column: x => x.PaymentCustomerId,
                    principalSchema: "payments",
                    principalTable: "PaymentCustomers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PaymentCustomers_TenantId_UserId_Provider",
            schema: "payments",
            table: "PaymentCustomers",
            columns: new[] { "TenantId", "UserId", "Provider" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SavedPaymentMethods_PaymentCustomerId_ProviderPaymentMethodId",
            schema: "payments",
            table: "SavedPaymentMethods",
            columns: new[] { "PaymentCustomerId", "ProviderPaymentMethodId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SavedPaymentMethods_TenantId_UserId_State",
            schema: "payments",
            table: "SavedPaymentMethods",
            columns: new[] { "TenantId", "UserId", "State" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SavedPaymentMethods", schema: "payments");
        migrationBuilder.DropTable(name: "PaymentCustomers", schema: "payments");
        migrationBuilder.DropColumn(name: "ProviderCustomerId", schema: "payments", table: "Payments");
        migrationBuilder.DropColumn(name: "ProviderPaymentMethodId", schema: "payments", table: "Payments");
        migrationBuilder.DropColumn(name: "SavePaymentMethodRequested", schema: "payments", table: "Payments");
    }
}
