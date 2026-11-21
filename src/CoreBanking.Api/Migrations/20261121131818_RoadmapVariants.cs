using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreBanking.Api.Migrations
{
    /// <inheritdoc />
    public partial class RoadmapVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "fee_waiver_reduced_balance",
                table: "products",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "last_limit_review_period",
                table: "overdraft_facilities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "penalty_interest_rate",
                table: "loan_products",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            // Seed the new pricing tiers. CUR-PER: full waiver >= 1000,
            // half fee (>= 500) between; loan products get penalty rates.
            migrationBuilder.Sql(@"
                UPDATE products SET fee_waiver_reduced_balance = 500
                 WHERE code = 'CUR-PER';
                UPDATE loan_products SET penalty_interest_rate = 0.06 WHERE code = 'PLN-PER';
                UPDATE loan_products SET penalty_interest_rate = 0.03 WHERE code = 'HML-STD';
                UPDATE loan_products SET penalty_interest_rate = 0.06 WHERE code = 'PLN-BDT';
                UPDATE loan_products SET penalty_interest_rate = 0.04 WHERE code = 'HML-BDT';

                INSERT INTO products (id, code, name, type, annual_interest_rate,
                                      monthly_fee, fee_waiver_min_balance,
                                      fee_waiver_reduced_balance, created_at, currency)
                VALUES (gen_random_uuid(), 'TD-STD', 'Term Deposit USD', 'TermDeposit',
                        0.050000, 0, 0, 0, now(), 'USD'),
                       (gen_random_uuid(), 'TD-BDT', 'Term Deposit BDT', 'TermDeposit',
                        0.080000, 0, 0, 0, now(), 'BDT')
                ON CONFLICT (code) DO NOTHING;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "fee_waiver_reduced_balance",
                table: "products");

            migrationBuilder.DropColumn(
                name: "last_limit_review_period",
                table: "overdraft_facilities");

            migrationBuilder.DropColumn(
                name: "penalty_interest_rate",
                table: "loan_products");
        }
    }
}
