using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "payments");

        migrationBuilder.CreateTable(
            name: "IdempotencyRecords",
            schema: "payments",
            columns: table => new
            {
                Key = table.Column<string>(type: "text", nullable: false),
                RequestHash = table.Column<string>(type: "text", nullable: false),
                ResponseJson = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IdempotencyRecords", x => x.Key);
            });

        migrationBuilder.CreateTable(
            name: "InboxState",
            schema: "payments",
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
            name: "JournalEntries",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Description = table.Column<string>(type: "text", nullable: false),
                Reference = table.Column<string>(type: "text", nullable: false),
                Currency = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_JournalEntries", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "LedgerAccounts",
            schema: "payments",
            columns: table => new
            {
                Code = table.Column<string>(type: "text", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Type = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LedgerAccounts", x => x.Code);
            });

        migrationBuilder.CreateTable(
            name: "OutboxState",
            schema: "payments",
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
            name: "Payments",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                PaymentIntentId = table.Column<string>(type: "text", nullable: false),
                AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                TaxMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                RefundedMinor = table.Column<long>(type: "bigint", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Payments", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Refunds",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                PaymentIntentId = table.Column<string>(type: "text", nullable: false),
                AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Refunds", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SyncRuns",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Reference = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                XeroJournalId = table.Column<string>(type: "text", nullable: true),
                Error = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SyncRuns", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "WebhookInbox",
            schema: "payments",
            columns: table => new
            {
                EventId = table.Column<string>(type: "text", nullable: false),
                ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WebhookInbox", x => x.EventId);
            });

        migrationBuilder.CreateTable(
            name: "JournalLines",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                AccountCode = table.Column<string>(type: "text", nullable: false),
                DebitMinor = table.Column<long>(type: "bigint", nullable: false),
                CreditMinor = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_JournalLines", x => x.Id);
                table.CheckConstraint("ck_line_nonneg", "\"DebitMinor\" >= 0 AND \"CreditMinor\" >= 0");
                table.CheckConstraint("ck_line_one_side", "(\"DebitMinor\" = 0) <> (\"CreditMinor\" = 0)");
                table.ForeignKey(
                    name: "FK_JournalLines_JournalEntries_EntryId",
                    column: x => x.EntryId,
                    principalSchema: "payments",
                    principalTable: "JournalEntries",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "OutboxMessage",
            schema: "payments",
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
                    principalSchema: "payments",
                    principalTable: "InboxState",
                    principalColumns: new[] { "MessageId", "ConsumerId" });
                table.ForeignKey(
                    name: "FK_OutboxMessage_OutboxState_OutboxId",
                    column: x => x.OutboxId,
                    principalSchema: "payments",
                    principalTable: "OutboxState",
                    principalColumn: "OutboxId");
            });

        migrationBuilder.CreateIndex(
            name: "IX_InboxState_Delivered",
            schema: "payments",
            table: "InboxState",
            column: "Delivered");

        migrationBuilder.CreateIndex(
            name: "IX_JournalEntries_Reference",
            schema: "payments",
            table: "JournalEntries",
            column: "Reference");

        migrationBuilder.CreateIndex(
            name: "IX_JournalLines_AccountCode",
            schema: "payments",
            table: "JournalLines",
            column: "AccountCode");

        migrationBuilder.CreateIndex(
            name: "IX_JournalLines_EntryId",
            schema: "payments",
            table: "JournalLines",
            column: "EntryId");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_EnqueueTime",
            schema: "payments",
            table: "OutboxMessage",
            column: "EnqueueTime");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_ExpirationTime",
            schema: "payments",
            table: "OutboxMessage",
            column: "ExpirationTime");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber",
            schema: "payments",
            table: "OutboxMessage",
            columns: new[] { "InboxMessageId", "InboxConsumerId", "SequenceNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_OutboxId_SequenceNumber",
            schema: "payments",
            table: "OutboxMessage",
            columns: new[] { "OutboxId", "SequenceNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OutboxState_Created",
            schema: "payments",
            table: "OutboxState",
            column: "Created");

        migrationBuilder.CreateIndex(
            name: "IX_Payments_OrderId",
            schema: "payments",
            table: "Payments",
            column: "OrderId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Payments_PaymentIntentId",
            schema: "payments",
            table: "Payments",
            column: "PaymentIntentId");

        migrationBuilder.CreateIndex(
            name: "IX_Refunds_OrderId",
            schema: "payments",
            table: "Refunds",
            column: "OrderId");

        migrationBuilder.CreateIndex(
            name: "IX_SyncRuns_Reference",
            schema: "payments",
            table: "SyncRuns",
            column: "Reference",
            unique: true);

        // NFR-1: every journal entry balances (deferred constraint trigger). Table refs are
        // schema-qualified; the trigger functions live in public and resolve the tables explicitly.
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION ledger_entry_balances() RETURNS trigger AS $$
            DECLARE d bigint; c bigint;
            BEGIN
                SELECT COALESCE(sum("DebitMinor"),0), COALESCE(sum("CreditMinor"),0)
                INTO d, c FROM payments."JournalLines" WHERE "EntryId" = NEW."EntryId";
                IF d <> c THEN
                    RAISE EXCEPTION 'Unbalanced journal entry %: debits=% credits=%', NEW."EntryId", d, c;
                END IF;
                RETURN NULL;
            END;
            $$ LANGUAGE plpgsql;
            """);
        migrationBuilder.Sql("""
            CREATE CONSTRAINT TRIGGER trg_ledger_balance
            AFTER INSERT ON payments."JournalLines"
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION ledger_entry_balances();
            """);
        // Append-only ledger: journal rows can never be updated or deleted.
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION ledger_append_only() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'Ledger is append-only: % on % is not allowed', TG_OP, TG_TABLE_NAME;
            END;
            $$ LANGUAGE plpgsql;
            """);
        migrationBuilder.Sql("""
            CREATE TRIGGER trg_entries_append_only BEFORE UPDATE OR DELETE ON payments."JournalEntries"
            FOR EACH ROW EXECUTE FUNCTION ledger_append_only();
            """);
        migrationBuilder.Sql("""
            CREATE TRIGGER trg_lines_append_only BEFORE UPDATE OR DELETE ON payments."JournalLines"
            FOR EACH ROW EXECUTE FUNCTION ledger_append_only();
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "IdempotencyRecords",
            schema: "payments");

        migrationBuilder.DropTable(
            name: "JournalLines",
            schema: "payments");

        migrationBuilder.DropTable(
            name: "LedgerAccounts",
            schema: "payments");

        migrationBuilder.DropTable(
            name: "OutboxMessage",
            schema: "payments");

        migrationBuilder.DropTable(
            name: "Payments",
            schema: "payments");

        migrationBuilder.DropTable(
            name: "Refunds",
            schema: "payments");

        migrationBuilder.DropTable(
            name: "SyncRuns",
            schema: "payments");

        migrationBuilder.DropTable(
            name: "WebhookInbox",
            schema: "payments");

        migrationBuilder.DropTable(
            name: "JournalEntries",
            schema: "payments");

        migrationBuilder.DropTable(
            name: "InboxState",
            schema: "payments");

        migrationBuilder.DropTable(
            name: "OutboxState",
            schema: "payments");
    }
}
