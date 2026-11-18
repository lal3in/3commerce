using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <summary>
/// payments."PaymentCustomers" under FORCE ROW LEVEL SECURITY (ADR-0024 amendment, owner decision 2026-11-15): each row
/// maps a member to their provider vault and may hold their email, and every saved card hangs off it, so it is
/// tenant-isolated by the database like <c>SavedPaymentMethods</c> (migration <c>SavedPaymentMethodsRls</c>, same policy
/// shape: platform scope bypasses; an empty app.tenant_id reads as NULL, no 22P02, and so matches no row). FORCE because
/// payments_svc owns the table (a non-superuser, non-BYPASSRLS role — infra/postgres/init-databases.sql). Every reader
/// and writer runs in a tenant scope through <c>PaymentsTenantScope</c> (<c>PaymentCustomerVault</c>,
/// <c>CheckoutInstrument</c>, <c>DefaultSavedCard</c>). A later data migration touching these rows must set platform
/// scope (<c>set_config('app.is_platform_admin', 'true', true)</c>) or it sees none of them.
/// </summary>
public partial class PaymentCustomersRls : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE payments."PaymentCustomers" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE payments."PaymentCustomers" FORCE ROW LEVEL SECURITY;
            CREATE POLICY "TenantIsolation_PaymentCustomers" ON payments."PaymentCustomers"
                USING (current_setting('app.is_platform_admin', true) = 'true'
                    OR "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK (current_setting('app.is_platform_admin', true) = 'true'
                    OR "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS "TenantIsolation_PaymentCustomers" ON payments."PaymentCustomers";
            ALTER TABLE payments."PaymentCustomers" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE payments."PaymentCustomers" DISABLE ROW LEVEL SECURITY;
            """);
    }
}
