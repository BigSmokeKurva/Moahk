using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Moahk.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRefProgramAndModelPercent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "Status",
                table: "Users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<int>(
                name: "ProfitPercent",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 10,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<double>(
                name: "PriceMax",
                table: "Users",
                type: "double precision",
                nullable: false,
                defaultValue: 10000.0,
                oldClrType: typeof(double),
                oldType: "double precision");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "License",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<int>(
                name: "Criteria",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<double>(
                name: "ModelPercentMax",
                table: "Users",
                type: "double precision",
                nullable: false,
                defaultValue: 100.0);

            migrationBuilder.AddColumn<double>(
                name: "ModelPercentMin",
                table: "Users",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "ReferralBalance",
                table: "Users",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "ReferralPercent",
                table: "Users",
                type: "double precision",
                nullable: false,
                defaultValue: 25.0);

            migrationBuilder.AddColumn<long>(
                name: "ReferrerId",
                table: "Users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Amount",
                table: "CrystalpayInvoices",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ModelPercentMax",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ModelPercentMin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReferralBalance",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReferralPercent",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReferrerId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Amount",
                table: "CrystalpayInvoices");

            migrationBuilder.AlterColumn<long>(
                name: "Status",
                table: "Users",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<int>(
                name: "ProfitPercent",
                table: "Users",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 10);

            migrationBuilder.AlterColumn<double>(
                name: "PriceMax",
                table: "Users",
                type: "double precision",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "double precision",
                oldDefaultValue: 10000.0);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "License",
                table: "Users",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Criteria",
                table: "Users",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 0);
        }
    }
}
