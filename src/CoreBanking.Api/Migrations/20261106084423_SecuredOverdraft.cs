using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreBanking.Api.Migrations
{
    /// <inheritdoc />
    public partial class SecuredOverdraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "collateral_account_id",
                table: "overdraft_facilities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "collateral_amount",
                table: "overdraft_facilities",
                type: "numeric(19,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "product_code",
                table: "overdraft_facilities",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "ODL-VAR");

            migrationBuilder.CreateIndex(
                name: "ix_overdraft_facilities_collateral_account_id",
                table: "overdraft_facilities",
                column: "collateral_account_id");

            migrationBuilder.AddForeignKey(
                name: "fk_overdraft_facilities_accounts_collateral_account_id",
                table: "overdraft_facilities",
                column: "collateral_account_id",
                principalTable: "accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_overdraft_facilities_accounts_collateral_account_id",
                table: "overdraft_facilities");

            migrationBuilder.DropIndex(
                name: "ix_overdraft_facilities_collateral_account_id",
                table: "overdraft_facilities");

            migrationBuilder.DropColumn(
                name: "collateral_account_id",
                table: "overdraft_facilities");

            migrationBuilder.DropColumn(
                name: "collateral_amount",
                table: "overdraft_facilities");

            migrationBuilder.DropColumn(
                name: "product_code",
                table: "overdraft_facilities");
        }
    }
}
