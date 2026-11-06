using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreBanking.Api.Migrations
{
    /// <inheritdoc />
    public partial class OverdraftPartialUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_overdraft_facilities_account_id",
                table: "overdraft_facilities");

            migrationBuilder.CreateIndex(
                name: "ix_overdraft_facilities_account_id",
                table: "overdraft_facilities",
                column: "account_id",
                unique: true,
                filter: "status NOT IN ('Rejected', 'Closed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_overdraft_facilities_account_id",
                table: "overdraft_facilities");

            migrationBuilder.CreateIndex(
                name: "ix_overdraft_facilities_account_id",
                table: "overdraft_facilities",
                column: "account_id",
                unique: true);
        }
    }
}
