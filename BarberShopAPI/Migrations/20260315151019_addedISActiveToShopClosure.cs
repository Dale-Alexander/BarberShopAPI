using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class addedISActiveToShopClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            /* Renames ShopClosure -> ShopClosures and adds IsActive.
             *
             * These operations were previously commented out, because as generated they dropped the
             * foreign key and primary key while naming the table by its POST-rename name ("ShopClosures")
             * - before the rename had happened. That fails, so the change was applied to the dev database
             * by hand instead and the code was left commented. The cost was that the migration chain no
             * longer replayed onto an empty database: every later migration targets "ShopClosures", a
             * table nothing ever created, so a fresh deploy died on the first of them.
             *
             * Restored here in an order that actually works: drop the constraints off the OLD table name,
             * rename, then recreate them under the new name. Already-migrated databases are unaffected -
             * __EFMigrationsHistory has this migration recorded, so it is never re-run. */
            migrationBuilder.DropForeignKey(
                name: "FK_ShopClosure_Barbers_BarberId",
                table: "ShopClosure");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ShopClosure",
                table: "ShopClosure");

            migrationBuilder.DropIndex(
                name: "IX_ShopClosure_BarberId",
                table: "ShopClosure");

            migrationBuilder.RenameTable(
                name: "ShopClosure",
                newName: "ShopClosures");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "ShopClosures",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ShopClosures",
                table: "ShopClosures",
                column: "Id");

            // No plain IX_ShopClosures_BarberId is recreated: the composite index below already leads with
            // BarberId, which is why EF omits a separate foreign-key index for it in the model.
            migrationBuilder.CreateIndex(
                name: "IX_ShopClosures_BarberId_Date",
                table: "ShopClosures",
                columns: new[] { "BarberId", "Date" },
                unique: true,
                filter: "[BarberId] IS NOT NULL AND [IsFullDay] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ShopClosures_Date",
                table: "ShopClosures",
                column: "Date",
                unique: true,
                filter: "[BarberId] IS NULL AND [IsFullDay] = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_ShopClosures_Barbers_BarberId",
                table: "ShopClosures",
                column: "BarberId",
                principalTable: "Barbers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Mirror of Up: drop under the new names, rename back, recreate under the old ones.
            migrationBuilder.DropForeignKey(
                name: "FK_ShopClosures_Barbers_BarberId",
                table: "ShopClosures");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ShopClosures",
                table: "ShopClosures");

            migrationBuilder.DropIndex(
                name: "IX_ShopClosures_BarberId_Date",
                table: "ShopClosures");

            migrationBuilder.DropIndex(
                name: "IX_ShopClosures_Date",
                table: "ShopClosures");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "ShopClosures");

            migrationBuilder.RenameTable(
                name: "ShopClosures",
                newName: "ShopClosure");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ShopClosure",
                table: "ShopClosure",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_ShopClosure_BarberId",
                table: "ShopClosure",
                column: "BarberId");

            migrationBuilder.AddForeignKey(
                name: "FK_ShopClosure_Barbers_BarberId",
                table: "ShopClosure",
                column: "BarberId",
                principalTable: "Barbers",
                principalColumn: "Id");
        }
    }
}
