using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CoreBanking.Api.Migrations
{
    /// <inheritdoc />
    public partial class OverdraftCredit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "overdraft_facilities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    limit = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    annual_interest_rate = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    credit_score = table.Column<int>(type: "integer", nullable: false),
                    decision_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    applied_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    close_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_overdraft_facilities", x => x.id);
                    table.ForeignKey(
                        name: "fk_overdraft_facilities_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "overdraft_accruals",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accrual_date = table.Column<DateTime>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    charged = table.Column<bool>(type: "boolean", nullable: false),
                    charged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    charge_transaction_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_overdraft_accruals", x => x.id);
                    table.ForeignKey(
                        name: "fk_overdraft_accruals_overdraft_facilities_facility_id",
                        column: x => x.facility_id,
                        principalTable: "overdraft_facilities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_overdraft_accruals_charged",
                table: "overdraft_accruals",
                column: "charged");

            migrationBuilder.CreateIndex(
                name: "ix_overdraft_accruals_facility_id_accrual_date",
                table: "overdraft_accruals",
                columns: new[] { "facility_id", "accrual_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_overdraft_facilities_account_id",
                table: "overdraft_facilities",
                column: "account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_overdraft_facilities_reference",
                table: "overdraft_facilities",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_overdraft_facilities_status",
                table: "overdraft_facilities",
                column: "status");
            // ---------- new GL account: Interest Receivable (asset) ----------
            migrationBuilder.Sql(@"
                INSERT INTO gl_accounts (id, code, name, type)
                VALUES (gen_random_uuid(), '1010', 'Interest Receivable', 'Asset');
            ");

        } // end Up

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "overdraft_accruals");

            migrationBuilder.DropTable(
                name: "overdraft_facilities");
        }
    }
}
