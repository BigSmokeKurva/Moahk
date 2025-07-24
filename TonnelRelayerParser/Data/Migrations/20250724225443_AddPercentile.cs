using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Moahk.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPercentile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Percentile",
                table: "Users",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Percentile",
                table: "Users");
        }
    }
}
