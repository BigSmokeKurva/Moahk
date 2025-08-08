using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Moahk.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCriteriaTemp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Criteria",
                table: "Users");

            migrationBuilder.AddColumn<long[]>(
                name: "CriteriaTemp",
                table: "Users",
                type: "bigint[]",
                nullable: false,
                defaultValue: new long[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CriteriaTemp",
                table: "Users");

            migrationBuilder.AddColumn<long>(
                name: "Criteria",
                table: "Users",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);
        }
    }
}
