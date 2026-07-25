using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class LedgerLineCurrency : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Currency",
            schema: "payments",
            table: "JournalLines",
            type: "character varying(3)",
            maxLength: 3,
            nullable: false,
            defaultValue: "");

        // Backfill each existing line's currency from its entry. JournalLines is append-only
        // (trg_lines_append_only blocks UPDATE), so disable user triggers for this one-off backfill.
        migrationBuilder.Sql(@"
                ALTER TABLE payments.""JournalLines"" DISABLE TRIGGER USER;
                UPDATE payments.""JournalLines"" l SET ""Currency"" = e.""Currency""
                FROM payments.""JournalEntries"" e WHERE e.""Id"" = l.""EntryId"";
                ALTER TABLE payments.""JournalLines"" ENABLE TRIGGER USER;");

        migrationBuilder.CreateIndex(
            name: "IX_JournalLines_AccountCode_Currency",
            schema: "payments",
            table: "JournalLines",
            columns: new[] { "AccountCode", "Currency" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_JournalLines_AccountCode_Currency",
            schema: "payments",
            table: "JournalLines");

        migrationBuilder.DropColumn(
            name: "Currency",
            schema: "payments",
            table: "JournalLines");
    }
}
