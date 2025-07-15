using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Moahk.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPromoCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PromoCodeCode",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PromoCodes",
                columns: table => new
                {
                    Code = table.Column<string>(type: "text", nullable: false),
                    Percent = table.Column<double>(type: "double precision", nullable: false),
                    MaxUses = table.Column<int>(type: "integer", nullable: true),
                    DateExpiration = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UsedUsersIds = table.Column<List<long>>(type: "bigint[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromoCodes", x => x.Code);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_PromoCodeCode",
                table: "Users",
                column: "PromoCodeCode");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_PromoCodes_PromoCodeCode",
                table: "Users",
                column: "PromoCodeCode",
                principalTable: "PromoCodes",
                principalColumn: "Code");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_PromoCodes_PromoCodeCode",
                table: "Users");

            migrationBuilder.DropTable(
                name: "PromoCodes");

            migrationBuilder.DropIndex(
                name: "IX_Users_PromoCodeCode",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PromoCodeCode",
                table: "Users");
        }
    }
}
