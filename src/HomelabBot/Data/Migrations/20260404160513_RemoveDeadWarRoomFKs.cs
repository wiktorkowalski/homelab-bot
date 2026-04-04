using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomelabBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDeadWarRoomFKs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HealingChainId",
                table: "WarRooms");

            migrationBuilder.DropColumn(
                name: "InvestigationId",
                table: "WarRooms");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HealingChainId",
                table: "WarRooms",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InvestigationId",
                table: "WarRooms",
                type: "INTEGER",
                nullable: true);
        }
    }
}
