using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreBanking.Api.Migrations
{
    /// <inheritdoc />
    public partial class BdtAndCib : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "currency",
                table: "products",
                type: "character(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                defaultValue: "USD");

            migrationBuilder.AlterColumn<string>(
                name: "product_code",
                table: "overdraft_facilities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10);

            migrationBuilder.AddColumn<int>(
                name: "cib_score",
                table: "overdraft_facilities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "score_source",
                table: "overdraft_facilities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Internal");

            migrationBuilder.AddColumn<int>(
                name: "cib_score",
                table: "loans",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "score_source",
                table: "loans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Internal");

            // ---------- Bangladesh: account products in BDT ----------
            migrationBuilder.Sql(@"
                INSERT INTO products (id, code, name, type, currency, annual_interest_rate, monthly_fee, fee_waiver_min_balance, created_at)
                VALUES
                  ('33333333-3333-3333-3333-333333333333', 'SAV-BDT', 'Savings Standard (BDT)', 'Savings', 'BDT', 0.025000, 100.00, 50000.00, now()),
                  ('44444444-4444-4444-4444-444444444444', 'CUR-BDT', 'Current Personal (BDT)', 'Current', 'BDT', 0.000000, 300.00, 50000.00, now());
            ");

            // ---------- Bangladesh: FX rate to the USD GL base ----------
            migrationBuilder.Sql(@"
                INSERT INTO fx_rates (from_currency, to_currency, rate, as_of)
                VALUES
                  ('BDT', 'USD', 0.00840000, '2026-01-01T00:00:00Z');
            ");

            // ---------- Bangladesh: loan products in BDT ----------
            migrationBuilder.Sql(@"
                INSERT INTO loan_products
                  (id, code, name, status, currency, min_principal, max_principal,
                   min_term_months, max_term_months, annual_interest_rate,
                   origination_fee, min_credit_score, created_at)
                VALUES
                  ('55555555-5555-5555-5555-555555555555', 'PLN-BDT', 'Personal Loan (BDT)', 'Active', 'BDT',
                   50000.00, 5000000.00, 6, 60, 0.150000, 5000.00, 650, now()),
                  ('66666666-6666-6666-6666-666666666666', 'HML-BDT', 'Home Loan (BDT)', 'Active', 'BDT',
                   5000000.00, 60000000.00, 60, 360, 0.075000, 50000.00, 700, now());
            ");

            // Backfill product currency for seeded rows (migration default already USD, explicit for clarity)
            migrationBuilder.Sql(@"
                UPDATE products SET currency = 'USD' WHERE currency IS NULL OR currency = '';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "currency",
                table: "products");

            migrationBuilder.DropColumn(
                name: "cib_score",
                table: "overdraft_facilities");

            migrationBuilder.DropColumn(
                name: "score_source",
                table: "overdraft_facilities");

            migrationBuilder.DropColumn(
                name: "cib_score",
                table: "loans");

            migrationBuilder.DropColumn(
                name: "score_source",
                table: "loans");

            migrationBuilder.AlterColumn<string>(
                name: "product_code",
                table: "overdraft_facilities",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);
        }
    }
}
