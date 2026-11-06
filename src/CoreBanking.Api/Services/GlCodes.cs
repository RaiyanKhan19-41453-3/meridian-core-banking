namespace CoreBanking.Api.Services;

/// <summary>GL account codes (seeded). USD-base chart of accounts.</summary>
public static class GlCodes
{
    public const string VaultCash = "1001";          // Asset
    public const string InterestReceivable = "1010"; // Asset
    public const string LoansReceivable = "1201";    // Asset
    public const string CustomerDeposits = "2001";   // Liability
    public const string InterestPayable = "2101";    // Liability
    public const string FeeIncome = "4001";          // Revenue
    public const string LoanInterestIncome = "6101"; // Revenue
    public const string InterestExpense = "5001";    // Expense

    public static string CodeForAccountNumber(string accountNumber) =>
        accountNumber switch
        {
            var n when n.EndsWith("-OMNIBUS", StringComparison.OrdinalIgnoreCase) => VaultCash,
            var n when n.Contains("-LOANCLR", StringComparison.OrdinalIgnoreCase) => LoansReceivable,
            var n when n.Contains("-INTCLR", StringComparison.OrdinalIgnoreCase) => InterestPayable,
            var n when n.Contains("-FEECLR", StringComparison.OrdinalIgnoreCase) => FeeIncome,
            var n when n.StartsWith("AC", StringComparison.OrdinalIgnoreCase) => CustomerDeposits,
            var n when n.StartsWith("SYS-", StringComparison.OrdinalIgnoreCase) => VaultCash,
            _ => throw new BankException("gl_unmapped_account",
                $"No GL mapping for account {accountNumber}.", 500)
        };

    public static string NameFor(string code) => code switch
    {
        VaultCash => "Vault Cash & Settlement",
        LoansReceivable => "Loans Receivable",
        CustomerDeposits => "Customer Deposits",
        InterestPayable => "Interest Payable",
        FeeIncome => "Fee & Commission Income",
        LoanInterestIncome => "Loan Interest Income",
        InterestExpense => "Interest Expense",
        _ => code
    };
}
