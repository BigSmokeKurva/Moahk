using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Moahk.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiCriteria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "CriteriaTemp",
                table: "Users",
                newName: "Criteria");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Criteria",
                table: "Users",
                newName: "CriteriaTemp");
        }
    }
}
