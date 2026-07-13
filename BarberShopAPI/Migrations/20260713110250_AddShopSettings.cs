using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddShopSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShopSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BufferMin = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "ShopSettings",
                columns: new[] { "Id", "BufferMin" },
                values: new object[] { 1, 0 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShopSettings");
        }
    }
}
