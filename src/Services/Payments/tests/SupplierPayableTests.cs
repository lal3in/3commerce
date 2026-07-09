using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Tests;

public class SupplierPayableTests
{
    [Fact]
    public void Supplier_bank_account_requires_approval_before_payout_instruction()
    {
        var bank = NewBankAccount();

        Assert.Throws<SupplierPayableRuleException>(() => PayoutInstruction.Create(bank, PayoutCadence.Weekly, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Supplier_bank_account_approval_requires_reason()
    {
        var bank = NewBankAccount();

        Assert.Throws<SupplierPayableRuleException>(() => bank.Approve(" ", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Payout_instruction_uses_approved_bank_account_route()
    {
        var bank = NewBankAccount();
        bank.Approve("verified by operator", DateTimeOffset.UtcNow);

        var instruction = PayoutInstruction.Create(bank, PayoutCadence.Fortnightly, DateTimeOffset.UtcNow);

        Assert.Equal(bank.TenantId, instruction.TenantId);
        Assert.Equal(bank.SupplierEntityId, instruction.SupplierEntityId);
        Assert.Equal(bank.Id, instruction.BankAccountId);
        Assert.True(instruction.Active);
    }

    [Fact]
    public void Supplier_payable_policy_calculates_net_after_commission()
    {
        var policy = NewPolicy(commissionBps: 1_500);

        var payable = SupplierPayable.Create(policy.TenantId, policy.SupplierEntityId, Guid.CreateVersion7(), 10_000, "AUD", policy, DateTimeOffset.UtcNow);

        Assert.Equal(1_500, payable.CommissionMinor);
        Assert.Equal(8_500, payable.NetPayableMinor);
    }

    [Fact]
    public void Supplier_payable_rejects_cross_supplier_policy()
    {
        var policy = NewPolicy(commissionBps: 1_000);

        Assert.Throws<SupplierPayableRuleException>(() =>
            SupplierPayable.Create(policy.TenantId, Guid.CreateVersion7(), Guid.CreateVersion7(), 10_000, "AUD", policy, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Supplier_payable_posts_balanced_accrual_entry()
    {
        var policy = NewPolicy(commissionBps: 2_000);
        var payable = SupplierPayable.Create(policy.TenantId, policy.SupplierEntityId, Guid.CreateVersion7(), 10_000, "AUD", policy, DateTimeOffset.UtcNow);

        var entry = payable.ToAccrualEntry(DateTimeOffset.UtcNow);

        Assert.Equal(entry.Lines.Sum(l => l.DebitMinor), entry.Lines.Sum(l => l.CreditMinor));
        Assert.Contains(entry.Lines, l => l.AccountCode == Accounts.ExpenseCostOfGoodsSold && l.DebitMinor == 8_000);
        Assert.Contains(entry.Lines, l => l.AccountCode == Accounts.LiabilitySupplierPayable && l.CreditMinor == 8_000);
    }

    private static SupplierBankAccount NewBankAccount() =>
        SupplierBankAccount.Create(
            tenantId: Guid.CreateVersion7(),
            supplierEntityId: Guid.CreateVersion7(),
            accountName: "Supplier Pty Ltd",
            bankCountry: "AU",
            routingNumberMasked: "***-123",
            accountNumberMasked: "******789",
            accountTokenRef: "vault:supplier-bank:123",
            now: DateTimeOffset.UtcNow);

    private static SupplierPayablePolicy NewPolicy(int commissionBps)
    {
        var tenantId = Guid.CreateVersion7();
        var supplierId = Guid.CreateVersion7();
        return new SupplierPayablePolicy
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SupplierEntityId = supplierId,
            CommissionBps = commissionBps,
            Cadence = PayoutCadence.Weekly,
        };
    }
}
