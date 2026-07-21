using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class PaymentMethodKind : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "MethodKind",
            schema: "payments",
            table: "Payments",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<string>(
            name: "Provider",
            schema: "payments",
            table: "Payments",
            type: "character varying(40)",
            maxLength: 40,
            nullable: false,
            defaultValue: "stripe");

        // The ledger now posts cash/fees to the settling PSP's own accounts. ChartOfAccountsSeeder
        // only seeds an EMPTY chart, so already-seeded databases need the pay_4 provider accounts
        // backfilled here. Idempotent; stripe's pair already exists and is left untouched.
        // AccountType: 1 = Asset, 4 = Expense.
        migrationBuilder.Sql("""
            INSERT INTO payments."LedgerAccounts" ("Code", "Name", "Type")
            VALUES ('cash.polar', 'Cash — Polar', 1),
                   ('cash.paypal', 'Cash — PayPal', 1),
                   ('cash.afterpay', 'Cash — Afterpay', 1),
                   ('expense.polar_fees', 'Polar fees', 4),
                   ('expense.paypal_fees', 'PayPal fees', 4),
                   ('expense.afterpay_fees', 'Afterpay fees', 4)
            ON CONFLICT ("Code") DO NOTHING;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Only the unused provider accounts go away; any that carry postings are kept so the
        // append-only ledger never references a missing code.
        migrationBuilder.Sql("""
            DELETE FROM payments."LedgerAccounts" a
            WHERE a."Code" IN ('cash.polar', 'cash.paypal', 'cash.afterpay',
                               'expense.polar_fees', 'expense.paypal_fees', 'expense.afterpay_fees')
              AND NOT EXISTS (SELECT 1 FROM payments."JournalLines" l WHERE l."AccountCode" = a."Code");
            """);

        migrationBuilder.DropColumn(
            name: "MethodKind",
            schema: "payments",
            table: "Payments");

        migrationBuilder.DropColumn(
            name: "Provider",
            schema: "payments",
            table: "Payments");
    }
}
