using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Support.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SupportTicketsRma : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Rmas",
            columns: table => new
            {
                CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                CurrentState = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                Email = table.Column<string>(type: "text", nullable: true),
                AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                Reason = table.Column<string>(type: "text", nullable: true),
                RefundId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Rmas", x => x.CorrelationId);
            });

        migrationBuilder.CreateTable(
            name: "Tickets",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                Email = table.Column<string>(type: "text", nullable: false),
                Reason = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Tickets", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "TicketMessages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                Author = table.Column<int>(type: "integer", nullable: false),
                Body = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TicketMessages", x => x.Id);
                table.ForeignKey(
                    name: "FK_TicketMessages_Tickets_TicketId",
                    column: x => x.TicketId,
                    principalTable: "Tickets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Rmas_OrderId",
            table: "Rmas",
            column: "OrderId");

        migrationBuilder.CreateIndex(
            name: "IX_Rmas_RefundId",
            table: "Rmas",
            column: "RefundId");

        migrationBuilder.CreateIndex(
            name: "IX_TicketMessages_TicketId",
            table: "TicketMessages",
            column: "TicketId");

        migrationBuilder.CreateIndex(
            name: "IX_Tickets_OrderId",
            table: "Tickets",
            column: "OrderId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Rmas");

        migrationBuilder.DropTable(
            name: "TicketMessages");

        migrationBuilder.DropTable(
            name: "Tickets");
    }
}
