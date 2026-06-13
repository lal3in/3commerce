using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class PaymentsLedger : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "JournalEntries",
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
            name: "Payments",
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
            name: "WebhookInbox",
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
                    principalTable: "JournalEntries",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_JournalEntries_Reference",
            table: "JournalEntries",
            column: "Reference");

        migrationBuilder.CreateIndex(
            name: "IX_JournalLines_AccountCode",
            table: "JournalLines",
            column: "AccountCode");

        migrationBuilder.CreateIndex(
            name: "IX_JournalLines_EntryId",
            table: "JournalLines",
            column: "EntryId");

        migrationBuilder.CreateIndex(
            name: "IX_Payments_OrderId",
            table: "Payments",
            column: "OrderId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Payments_PaymentIntentId",
            table: "Payments",
            column: "PaymentIntentId");

        migrationBuilder.CreateIndex(
            name: "IX_Refunds_OrderId",
            table: "Refunds",
            column: "OrderId");

        // NFR-1: every journal entry balances (Σ debits = Σ credits). Checked at COMMIT
        // via a DEFERRED constraint trigger, because lines are inserted after the entry.
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION ledger_entry_balances() RETURNS trigger AS $$
            DECLARE d bigint; c bigint;
            BEGIN
                SELECT COALESCE(sum("DebitMinor"),0), COALESCE(sum("CreditMinor"),0)
                INTO d, c FROM "JournalLines" WHERE "EntryId" = NEW."EntryId";
                IF d <> c THEN
                    RAISE EXCEPTION 'Unbalanced journal entry %: debits=% credits=%', NEW."EntryId", d, c;
                END IF;
                RETURN NULL;
            END;
            $$ LANGUAGE plpgsql;
            """);
        migrationBuilder.Sql("""
            CREATE CONSTRAINT TRIGGER trg_ledger_balance
            AFTER INSERT ON "JournalLines"
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION ledger_entry_balances();
            """);

        // Append-only ledger: journal rows can never be updated or deleted (corrections
        // are new reversing entries). Enforced by trigger so it holds even for the owner.
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION ledger_append_only() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'Ledger is append-only: % on % is not allowed', TG_OP, TG_TABLE_NAME;
            END;
            $$ LANGUAGE plpgsql;
            """);
        migrationBuilder.Sql("""
            CREATE TRIGGER trg_entries_append_only BEFORE UPDATE OR DELETE ON "JournalEntries"
            FOR EACH ROW EXECUTE FUNCTION ledger_append_only();
            """);
        migrationBuilder.Sql("""
            CREATE TRIGGER trg_lines_append_only BEFORE UPDATE OR DELETE ON "JournalLines"
            FOR EACH ROW EXECUTE FUNCTION ledger_append_only();
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_lines_append_only ON \"JournalLines\";");
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_entries_append_only ON \"JournalEntries\";");
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_ledger_balance ON \"JournalLines\";");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS ledger_append_only();");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS ledger_entry_balances();");

        migrationBuilder.DropTable(
            name: "JournalLines");

        migrationBuilder.DropTable(
            name: "LedgerAccounts");

        migrationBuilder.DropTable(
            name: "Payments");

        migrationBuilder.DropTable(
            name: "Refunds");

        migrationBuilder.DropTable(
            name: "WebhookInbox");

        migrationBuilder.DropTable(
            name: "JournalEntries");
    }
}
