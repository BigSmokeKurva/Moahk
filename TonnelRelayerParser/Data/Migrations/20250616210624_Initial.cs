using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Moahk.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    License = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PriceMin = table.Column<double>(type: "double precision", nullable: false),
                    PriceMax = table.Column<double>(type: "double precision", nullable: false),
                    ProfitPercent = table.Column<int>(type: "integer", nullable: false),
                    Criteria = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<long>(type: "bigint", nullable: false),
                    IsStarted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
