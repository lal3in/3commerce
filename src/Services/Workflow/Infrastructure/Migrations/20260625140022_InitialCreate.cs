using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ThreeCommerce.Workflow.Infrastructure.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "workflow");

        migrationBuilder.CreateTable(
            name: "InboxState",
            schema: "workflow",
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
            schema: "workflow",
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
            name: "Runs",
            schema: "workflow",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                JobName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Runs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "OutboxMessage",
            schema: "workflow",
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
                    principalSchema: "workflow",
                    principalTable: "InboxState",
                    principalColumns: new[] { "MessageId", "ConsumerId" });
                table.ForeignKey(
                    name: "FK_OutboxMessage_OutboxState_OutboxId",
                    column: x => x.OutboxId,
                    principalSchema: "workflow",
                    principalTable: "OutboxState",
                    principalColumn: "OutboxId");
            });

        migrationBuilder.CreateIndex(
            name: "IX_InboxState_Delivered",
            schema: "workflow",
            table: "InboxState",
            column: "Delivered");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_EnqueueTime",
            schema: "workflow",
            table: "OutboxMessage",
            column: "EnqueueTime");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_ExpirationTime",
            schema: "workflow",
            table: "OutboxMessage",
            column: "ExpirationTime");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber",
            schema: "workflow",
            table: "OutboxMessage",
            columns: new[] { "InboxMessageId", "InboxConsumerId", "SequenceNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_OutboxId_SequenceNumber",
            schema: "workflow",
            table: "OutboxMessage",
            columns: new[] { "OutboxId", "SequenceNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OutboxState_Created",
            schema: "workflow",
            table: "OutboxState",
            column: "Created");

        migrationBuilder.CreateIndex(
            name: "IX_Runs_JobName_StartedAt",
            schema: "workflow",
            table: "Runs",
            columns: new[] { "JobName", "StartedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "OutboxMessage",
            schema: "workflow");

        migrationBuilder.DropTable(
            name: "Runs",
            schema: "workflow");

        migrationBuilder.DropTable(
            name: "InboxState",
            schema: "workflow");

        migrationBuilder.DropTable(
            name: "OutboxState",
            schema: "workflow");
    }
}
