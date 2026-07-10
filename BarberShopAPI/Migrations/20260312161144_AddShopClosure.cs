using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddShopClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ShopClosure_Barbers_barberId",
                table: "ShopClosure");

            migrationBuilder.RenameColumn(
                name: "reason",
                table: "ShopClosure",
                newName: "Reason");

            migrationBuilder.RenameColumn(
                name: "closureType",
                table: "ShopClosure",
                newName: "ClosureType");

            migrationBuilder.RenameColumn(
                name: "barberId",
                table: "ShopClosure",
                newName: "BarberId");

            migrationBuilder.RenameColumn(
                name: "Time",
                table: "ShopClosure",
                newName: "StartTime");

            migrationBuilder.RenameIndex(
                name: "IX_ShopClosure_barberId",
                table: "ShopClosure",
                newName: "IX_ShopClosure_BarberId");

            migrationBuilder.AddColumn<TimeOnly>(
                name: "EndTime",
                table: "ShopClosure",
                type: "time",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ShopClosure_Barbers_BarberId",
                table: "ShopClosure",
                column: "BarberId",
                principalTable: "Barbers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ShopClosure_Barbers_BarberId",
                table: "ShopClosure");

            migrationBuilder.DropColumn(
                name: "EndTime",
                table: "ShopClosure");

            migrationBuilder.RenameColumn(
                name: "Reason",
                table: "ShopClosure",
                newName: "reason");

            migrationBuilder.RenameColumn(
                name: "ClosureType",
                table: "ShopClosure",
                newName: "closureType");

            migrationBuilder.RenameColumn(
                name: "BarberId",
                table: "ShopClosure",
                newName: "barberId");

            migrationBuilder.RenameColumn(
                name: "StartTime",
                table: "ShopClosure",
                newName: "Time");

            migrationBuilder.RenameIndex(
                name: "IX_ShopClosure_BarberId",
                table: "ShopClosure",
                newName: "IX_ShopClosure_barberId");

            migrationBuilder.AddForeignKey(
                name: "FK_ShopClosure_Barbers_barberId",
                table: "ShopClosure",
                column: "barberId",
                principalTable: "Barbers",
                principalColumn: "Id");
        }
    }
}
