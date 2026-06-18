using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "ordering");

        migrationBuilder.CreateTable(
            name: "Carts",
            schema: "ordering",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CartKey = table.Column<string>(type: "text", nullable: true),
                UserId = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Carts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CheckoutStates",
            schema: "ordering",
            columns: table => new
            {
                CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                CurrentState = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PaymentIntentId = table.Column<string>(type: "text", nullable: true),
                AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                Email = table.Column<string>(type: "text", nullable: true),
                Currency = table.Column<string>(type: "text", nullable: true),
                TimeoutTokenId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CheckoutStates", x => x.CorrelationId);
            });

        migrationBuilder.CreateTable(
            name: "InboxState",
            schema: "ordering",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                ConsumerId = table.Column<Guid>(type: "uuid", nullable: false),
                LockId = table.Column<Guid>(type: "uuid", nullable: false),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                Received = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ReceiveCount = table.Column<int>(type: "integer", nullable: false),
                ExpirationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Consumed = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastSequenceNumber = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InboxState", x => x.Id);
                table.UniqueConstraint("AK_InboxState_MessageId_ConsumerId", x => new { x.MessageId, x.ConsumerId });
            });

        migrationBuilder.CreateTable(
            name: "Orders",
            schema: "ordering",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: true),
                Email = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                NetMinor = table.Column<long>(type: "bigint", nullable: false),
                TaxMinor = table.Column<long>(type: "bigint", nullable: false),
                ShippingMinor = table.Column<long>(type: "bigint", nullable: false),
                GrossMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                PaymentIntentId = table.Column<string>(type: "text", nullable: true),
                ShipName = table.Column<string>(type: "text", nullable: false),
                ShipLine1 = table.Column<string>(type: "text", nullable: false),
                ShipCity = table.Column<string>(type: "text", nullable: false),
                ShipPostcode = table.Column<string>(type: "text", nullable: false),
                ShipCountry = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Orders", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "OutboxState",
            schema: "ordering",
            columns: table => new
            {
                OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                LockId = table.Column<Guid>(type: "uuid", nullable: false),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastSequenceNumber = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboxState", x => x.OutboxId);
            });

        migrationBuilder.CreateTable(
            name: "ProductCopies",
            schema: "ordering",
            columns: table => new
            {
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                Slug = table.Column<string>(type: "text", nullable: false),
                Title = table.Column<string>(type: "text", nullable: false),
                MinPriceMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "text", nullable: false),
                ImageUrl = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductCopies", x => x.ProductId);
            });

        migrationBuilder.CreateTable(
            name: "CartItems",
            schema: "ordering",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CartId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                Slug = table.Column<string>(type: "text", nullable: false),
                Title = table.Column<string>(type: "text", nullable: false),
                ImageUrl = table.Column<string>(type: "text", nullable: true),
                UnitPriceMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "text", nullable: false),
                Quantity = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CartItems", x => x.Id);
                table.ForeignKey(
                    name: "FK_CartItems_Carts_CartId",
                    column: x => x.CartId,
                    principalSchema: "ordering",
                    principalTable: "Carts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "OrderLines",
            schema: "ordering",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                Title = table.Column<string>(type: "text", nullable: false),
                UnitPriceMinor = table.Column<long>(type: "bigint", nullable: false),
                Quantity = table.Column<int>(type: "integer", nullable: false),
                FulfillmentSource = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrderLines", x => x.Id);
                table.ForeignKey(
                    name: "FK_OrderLines_Orders_OrderId",
                    column: x => x.OrderId,
                    principalSchema: "ordering",
                    principalTable: "Orders",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "OutboxMessage",
            schema: "ordering",
            columns: table => new
            {
                SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                EnqueueTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                SentTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Headers = table.Column<string>(type: "text", nullable: true),
                Properties = table.Column<string>(type: "text", nullable: true),
                InboxMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                InboxConsumerId = table.Column<Guid>(type: "uuid", nullable: true),
                OutboxId = table.Column<Guid>(type: "uuid", nullable: true),
                MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                ContentType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                MessageType = table.Column<string>(type: "text", nullable: false),
                Body = table.Column<string>(type: "text", nullable: false),
                ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                CorrelationId = table.Column<Guid>(type: "uuid", nullable: true),
                InitiatorId = table.Column<Guid>(type: "uuid", nullable: true),
                RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                SourceAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                DestinationAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                ResponseAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                FaultAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                ExpirationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboxMessage", x => x.SequenceNumber);
                table.ForeignKey(
                    name: "FK_OutboxMessage_InboxState_InboxMessageId_InboxConsumerId",
                    columns: x => new { x.InboxMessageId, x.InboxConsumerId },
                    principalSchema: "ordering",
                    principalTable: "InboxState",
                    principalColumns: new[] { "MessageId", "ConsumerId" });
                table.ForeignKey(
                    name: "FK_OutboxMessage_OutboxState_OutboxId",
                    column: x => x.OutboxId,
                    principalSchema: "ordering",
                    principalTable: "OutboxState",
                    principalColumn: "OutboxId");
            });

        migrationBuilder.CreateIndex(
            name: "IX_CartItems_CartId",
            schema: "ordering",
            table: "CartItems",
            column: "CartId");

        migrationBuilder.CreateIndex(
            name: "IX_Carts_CartKey",
            schema: "ordering",
            table: "Carts",
            column: "CartKey");

        migrationBuilder.CreateIndex(
            name: "IX_Carts_UserId",
            schema: "ordering",
            table: "Carts",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_InboxState_Delivered",
            schema: "ordering",
            table: "InboxState",
            column: "Delivered");

        migrationBuilder.CreateIndex(
            name: "IX_OrderLines_OrderId",
            schema: "ordering",
            table: "OrderLines",
            column: "OrderId");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_EnqueueTime",
            schema: "ordering",
            table: "OutboxMessage",
            column: "EnqueueTime");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_ExpirationTime",
            schema: "ordering",
            table: "OutboxMessage",
            column: "ExpirationTime");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber",
            schema: "ordering",
            table: "OutboxMessage",
            columns: new[] { "InboxMessageId", "InboxConsumerId", "SequenceNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_OutboxId_SequenceNumber",
            schema: "ordering",
            table: "OutboxMessage",
            columns: new[] { "OutboxId", "SequenceNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OutboxState_Created",
            schema: "ordering",
            table: "OutboxState",
            column: "Created");

        migrationBuilder.CreateIndex(
            name: "IX_ProductCopies_Slug",
            schema: "ordering",
            table: "ProductCopies",
            column: "Slug");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CartItems",
            schema: "ordering");

        migrationBuilder.DropTable(
            name: "CheckoutStates",
            schema: "ordering");

        migrationBuilder.DropTable(
            name: "OrderLines",
            schema: "ordering");

        migrationBuilder.DropTable(
            name: "OutboxMessage",
            schema: "ordering");

        migrationBuilder.DropTable(
            name: "ProductCopies",
            schema: "ordering");

        migrationBuilder.DropTable(
            name: "Carts",
            schema: "ordering");

        migrationBuilder.DropTable(
            name: "Orders",
            schema: "ordering");

        migrationBuilder.DropTable(
            name: "InboxState",
            schema: "ordering");

        migrationBuilder.DropTable(
            name: "OutboxState",
            schema: "ordering");
    }
}
