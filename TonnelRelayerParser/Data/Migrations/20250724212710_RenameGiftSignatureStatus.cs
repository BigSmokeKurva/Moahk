using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Moahk.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameGiftSignatureStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "GiftSaleStatuses",
                table: "Users",
                newName: "GiftSignatureStatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "GiftSignatureStatus",
                table: "Users",
                newName: "GiftSaleStatuses");
        }
    }
}
