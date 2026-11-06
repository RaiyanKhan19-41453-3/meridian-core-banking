using CoreBanking.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<FxRate> FxRates => Set<FxRate>();
    public DbSet<GlAccount> GlAccounts => Set<GlAccount>();
    public DbSet<GlJournal> GlJournals => Set<GlJournal>();
    public DbSet<GlEntry> GlEntries => Set<GlEntry>();
    public DbSet<InterestAccrual> InterestAccruals => Set<InterestAccrual>();
    public DbSet<BatchRun> BatchRuns => Set<BatchRun>();
    public DbSet<CustomerProfile> CustomerProfiles => Set<CustomerProfile>();
    public DbSet<KycDocument> KycDocuments => Set<KycDocument>();
    public DbSet<WatchlistEntry> WatchlistEntries => Set<WatchlistEntry>();
    public DbSet<AmlAlert> AmlAlerts => Set<AmlAlert>();
    public DbSet<LoanProduct> LoanProducts => Set<LoanProduct>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<LoanInstallment> LoanInstallments => Set<LoanInstallment>();
    public DbSet<OverdraftFacility> OverdraftFacilities => Set<OverdraftFacility>();
    public DbSet<OverdraftAccrual> OverdraftAccruals => Set<OverdraftAccrual>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(256).IsRequired();
            e.Property(u => u.PasswordHash).IsRequired();
            e.Property(u => u.FullName).HasMaxLength(200).IsRequired();
            e.Property(u => u.Role).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<Account>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.AccountNumber).IsUnique();
            e.HasIndex(a => a.UserId);
            e.Property(a => a.AccountNumber).HasMaxLength(32).IsRequired();
            e.Property(a => a.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            e.Property(a => a.Balance).HasColumnType("numeric(19,4)").IsRequired();
            e.Property(a => a.Version).IsConcurrencyToken();
            e.HasOne(a => a.User).WithMany(u => u.Accounts)
                .HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Product).WithMany()
                .HasForeignKey(a => a.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(a => a.ProductId);
        });

        modelBuilder.Entity<Product>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.Code).IsUnique();
            e.Property(p => p.Code).HasMaxLength(20).IsRequired();
            e.Property(p => p.Name).HasMaxLength(100).IsRequired();
            e.Property(p => p.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.AnnualInterestRate).HasColumnType("numeric(9,6)");
            e.Property(p => p.MonthlyFee).HasColumnType("numeric(19,4)");
            e.Property(p => p.FeeWaiverMinBalance).HasColumnType("numeric(19,4)");
        });

        modelBuilder.Entity<FxRate>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.FromCurrency, r.ToCurrency, r.AsOf }).IsUnique();
            e.Property(r => r.FromCurrency).HasMaxLength(3).IsFixedLength();
            e.Property(r => r.ToCurrency).HasMaxLength(3).IsFixedLength();
            e.Property(r => r.Rate).HasColumnType("numeric(18,8)");
        });

        modelBuilder.Entity<GlAccount>(e =>
        {
            e.HasKey(g => g.Id);
            e.HasIndex(g => g.Code).IsUnique();
            e.Property(g => g.Code).HasMaxLength(10).IsRequired();
            e.Property(g => g.Name).HasMaxLength(100).IsRequired();
            e.Property(g => g.Type).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<GlJournal>(e =>
        {
            e.HasKey(j => j.Id);
            e.HasIndex(j => j.Number).IsUnique();
            e.HasIndex(j => j.BusinessDate);
            e.HasIndex(j => j.TransactionId);
            e.Property(j => j.Number).HasMaxLength(40).IsRequired();
            e.Property(j => j.Description).HasMaxLength(500);
            e.Property(j => j.UsdRate).HasColumnType("numeric(18,8)");
            e.HasOne<Transaction>().WithMany()
                .HasForeignKey(j => j.TransactionId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<GlEntry>(e =>
        {
            e.HasKey(g => g.Id);
            e.HasIndex(g => g.GlAccountId);
            e.HasIndex(g => g.JournalId);
            e.Property(g => g.Amount).HasColumnType("numeric(19,4)").IsRequired();
            e.HasOne(g => g.Journal).WithMany(j => j.Entries)
                .HasForeignKey(g => g.JournalId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(g => g.GlAccount).WithMany(a => a.Entries)
                .HasForeignKey(g => g.GlAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InterestAccrual>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.AccountId, a.AccrualDate }).IsUnique();
            e.HasIndex(a => a.Capitalized);
            e.Property(a => a.Amount).HasColumnType("numeric(19,4)").IsRequired();
        });

        modelBuilder.Entity<BatchRun>(e =>
        {
            e.HasKey(b => b.Id);
            e.HasIndex(b => new { b.JobType, b.RunDate }).IsUnique();
            e.Property(b => b.JobType).HasConversion<string>().HasMaxLength(20);
            e.Property(b => b.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(b => b.RunDate).HasColumnType("date");
        });

        modelBuilder.Entity<CustomerProfile>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.UserId).IsUnique();
            e.Property(p => p.FullLegalName).HasMaxLength(200).IsRequired();
            e.Property(p => p.Nationality).HasMaxLength(100).IsRequired();
            e.Property(p => p.Phone).HasMaxLength(40).IsRequired();
            e.Property(p => p.AddressLine).HasMaxLength(300).IsRequired();
            e.Property(p => p.City).HasMaxLength(100).IsRequired();
            e.Property(p => p.Country).HasMaxLength(100).IsRequired();
            e.Property(p => p.IdNumber).HasMaxLength(64).IsRequired();
            e.Property(p => p.AnnualIncomeBand).HasMaxLength(30).IsRequired();
            e.Property(p => p.IdType).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.EmploymentStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.KycStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.RiskRating).HasConversion<string>().HasMaxLength(10);
            e.Property(p => p.RejectionReason).HasMaxLength(500);
            e.Property(p => p.DateOfBirth).HasColumnType("date");
            e.HasOne(p => p.User).WithOne()
                .HasForeignKey<CustomerProfile>(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KycDocument>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.ProfileId);
            e.Property(d => d.FileName).HasMaxLength(255).IsRequired();
            e.Property(d => d.ContentType).HasMaxLength(100).IsRequired();
            e.Property(d => d.Sha256).HasMaxLength(64).IsRequired();
            e.Property(d => d.SizeBytes).IsRequired();
            e.Property(d => d.Content).IsRequired();
            e.Property(d => d.DocType).HasConversion<string>().HasMaxLength(20);
            e.HasOne(d => d.Profile).WithMany(p => p.Documents)
                .HasForeignKey(d => d.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WatchlistEntry>(e =>
        {
            e.HasKey(w => w.Id);
            e.HasIndex(w => w.FullName);
            e.Property(w => w.FullName).HasMaxLength(200).IsRequired();
            e.Property(w => w.Source).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<LoanProduct>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.Code).IsUnique();
            e.Property(p => p.Code).HasMaxLength(20).IsRequired();
            e.Property(p => p.Name).HasMaxLength(100).IsRequired();
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(10);
            e.Property(p => p.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(p => p.MinPrincipal).HasColumnType("numeric(19,4)");
            e.Property(p => p.MaxPrincipal).HasColumnType("numeric(19,4)");
            e.Property(p => p.AnnualInterestRate).HasColumnType("numeric(9,6)");
            e.Property(p => p.OriginationFee).HasColumnType("numeric(19,4)");
        });

        modelBuilder.Entity<Loan>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => l.Reference).IsUnique();
            e.HasIndex(l => l.UserId);
            e.HasIndex(l => l.Status);
            e.Property(l => l.Reference).HasMaxLength(30).IsRequired();
            e.Property(l => l.Purpose).HasMaxLength(500);
            e.Property(l => l.DecisionReason).HasMaxLength(500);
            e.Property(l => l.Status).HasConversion<string>().HasMaxLength(15);
            e.Property(l => l.Principal).HasColumnType("numeric(19,4)");
            e.Property(l => l.AnnualInterestRate).HasColumnType("numeric(9,6)");
            e.Property(l => l.OriginationFee).HasColumnType("numeric(19,4)");
            e.HasOne(l => l.LoanProduct).WithMany()
                .HasForeignKey(l => l.LoanProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany()
                .HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany()
                .HasForeignKey(l => l.DisbursementAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LoanInstallment>(e =>
        {
            e.HasKey(i => i.Id);
            e.HasIndex(i => new { i.LoanId, i.InstallmentNo }).IsUnique();
            e.HasIndex(i => new { i.Status, i.DueDate });
            e.Property(i => i.Principal).HasColumnType("numeric(19,4)");
            e.Property(i => i.Interest).HasColumnType("numeric(19,4)");
            e.Property(i => i.Total).HasColumnType("numeric(19,4)");
            e.Property(i => i.DueDate).HasColumnType("date");
            e.Property(i => i.Status).HasConversion<string>().HasMaxLength(15);
            e.HasOne(i => i.Loan).WithMany(l => l.Installments)
                .HasForeignKey(i => i.LoanId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OverdraftFacility>(e =>
        {
            e.HasKey(f => f.Id);
            e.HasIndex(f => f.Reference).IsUnique();
            // One ACTIVE/PENDING facility per account; re-application after Rejected/Closed is allowed
            e.HasIndex(f => f.AccountId).IsUnique().HasFilter("status NOT IN ('Rejected', 'Closed')");
            e.HasIndex(f => f.Status);
            e.Property(f => f.Reference).HasMaxLength(30).IsRequired();
            e.Property(f => f.Status).HasConversion<string>().HasMaxLength(15);
            e.Property(f => f.Limit).HasColumnType("numeric(19,4)").IsRequired();
            e.Property(f => f.AnnualInterestRate).HasColumnType("numeric(9,6)").IsRequired();
            e.Property(f => f.DecisionReason).HasMaxLength(500);
            e.Property(f => f.CloseReason).HasMaxLength(500);
            e.Property(f => f.ProductCode).HasMaxLength(10).IsRequired();
            e.Property(f => f.CollateralAmount).HasColumnType("numeric(19,4)");
            e.HasOne(f => f.Account).WithMany()
                .HasForeignKey(f => f.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(f => f.CollateralAccount).WithMany()
                .HasForeignKey(f => f.CollateralAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OverdraftAccrual>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.FacilityId, a.AccrualDate }).IsUnique();
            e.HasIndex(a => a.Charged);
            e.Property(a => a.Amount).HasColumnType("numeric(19,4)").IsRequired();
            e.Property(a => a.AccrualDate).HasColumnType("date");
            e.HasOne(a => a.Facility).WithMany(f => f.Accruals)
                .HasForeignKey(a => a.FacilityId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AmlAlert>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.Status);
            e.HasIndex(a => a.UserId);
            e.HasIndex(a => a.CreatedAt);
            e.Property(a => a.AlertType).HasConversion<string>().HasMaxLength(30);
            e.Property(a => a.Severity).HasConversion<string>().HasMaxLength(10);
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(10);
            e.Property(a => a.Details).HasColumnType("jsonb");
            e.Property(a => a.ReviewNote).HasMaxLength(1000);
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.TokenHash).IsUnique();
            e.HasIndex(r => r.UserId);
            e.Property(r => r.TokenHash).HasMaxLength(64).IsRequired();
            e.HasOne(r => r.User).WithMany(u => u.RefreshTokens)
                .HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Transaction>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.Reference).IsUnique();
            e.HasIndex(t => t.CreatedAt);
            e.Property(t => t.Reference).HasMaxLength(64).IsRequired();
            e.Property(t => t.RequestHash).HasMaxLength(64);
            e.Property(t => t.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.Description).HasMaxLength(500);
            e.HasOne<Transaction>().WithMany()
                .HasForeignKey(t => t.OriginalTransactionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LedgerEntry>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => l.AccountId);
            e.HasIndex(l => l.TransactionId);
            e.Property(l => l.Amount).HasColumnType("numeric(19,4)").IsRequired();
            e.Property(l => l.BalanceAfter).HasColumnType("numeric(19,4)").IsRequired();
            e.HasOne(l => l.Transaction).WithMany(t => t.Entries)
                .HasForeignKey(l => l.TransactionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.Account).WithMany(a => a.LedgerEntries)
                .HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.CreatedAt);
            e.Property(a => a.Action).HasMaxLength(100).IsRequired();
            e.Property(a => a.EntityName).HasMaxLength(100).IsRequired();
            e.Property(a => a.Payload).HasColumnType("jsonb");
        });
    }
}
