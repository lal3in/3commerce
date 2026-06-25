using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ThreeCommerce.Usage.Infrastructure.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "usage");

        migrationBuilder.CreateTable(
            name: "InboxState",
            schema: "usage",
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
            name: "OutboxState",
            schema: "usage",
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
            name: "UsageBalances",
            schema: "usage",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                CustomerEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Meter = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                IncludedQuantity = table.Column<long>(type: "bigint", nullable: false),
                UsedQuantity = table.Column<long>(type: "bigint", nullable: false),
                OverageAllowed = table.Column<bool>(type: "boolean", nullable: false),
                OverageUnitPriceMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                BilledOverageQuantity = table.Column<long>(type: "bigint", nullable: false),
                PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UsageBalances", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "UsageRecords",
            schema: "usage",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                BalanceId = table.Column<Guid>(type: "uuid", nullable: false),
                CustomerEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Meter = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Quantity = table.Column<long>(type: "bigint", nullable: false),
                ReferenceId = table.Column<string>(type: "text", nullable: true),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UsageRecords", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "OutboxMessage",
            schema: "usage",
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
                    principalSchema: "usage",
                    principalTable: "InboxState",
                    principalColumns: new[] { "MessageId", "ConsumerId" });
                table.ForeignKey(
                    name: "FK_OutboxMessage_OutboxState_OutboxId",
                    column: x => x.OutboxId,
                    principalSchema: "usage",
                    principalTable: "OutboxState",
                    principalColumn: "OutboxId");
            });

        migrationBuilder.CreateIndex(
            name: "IX_InboxState_Delivered",
            schema: "usage",
            table: "InboxState",
            column: "Delivered");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_EnqueueTime",
            schema: "usage",
            table: "OutboxMessage",
            column: "EnqueueTime");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_ExpirationTime",
            schema: "usage",
            table: "OutboxMessage",
            column: "ExpirationTime");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber",
            schema: "usage",
            table: "OutboxMessage",
            columns: new[] { "InboxMessageId", "InboxConsumerId", "SequenceNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_OutboxId_SequenceNumber",
            schema: "usage",
            table: "OutboxMessage",
            columns: new[] { "OutboxId", "SequenceNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OutboxState_Created",
            schema: "usage",
            table: "OutboxState",
            column: "Created");

        migrationBuilder.CreateIndex(
            name: "IX_UsageBalances_TenantId_CustomerEmail_Meter",
            schema: "usage",
            table: "UsageBalances",
            columns: new[] { "TenantId", "CustomerEmail", "Meter" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UsageRecords_BalanceId",
            schema: "usage",
            table: "UsageRecords",
            column: "BalanceId");

        migrationBuilder.CreateIndex(
            name: "IX_UsageRecords_TenantId_ReferenceId",
            schema: "usage",
            table: "UsageRecords",
            columns: new[] { "TenantId", "ReferenceId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "OutboxMessage",
            schema: "usage");

        migrationBuilder.DropTable(
            name: "UsageBalances",
            schema: "usage");

        migrationBuilder.DropTable(
            name: "UsageRecords",
            schema: "usage");

        migrationBuilder.DropTable(
            name: "InboxState",
            schema: "usage");

        migrationBuilder.DropTable(
            name: "OutboxState",
            schema: "usage");
    }
}
