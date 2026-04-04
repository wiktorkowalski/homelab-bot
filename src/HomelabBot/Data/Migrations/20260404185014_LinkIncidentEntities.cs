using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomelabBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkIncidentEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InvestigationId",
                table: "WarRooms",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InvestigationId",
                table: "AnomalyEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WarRooms_InvestigationId",
                table: "WarRooms",
                column: "InvestigationId");

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyEvents_InvestigationId",
                table: "AnomalyEvents",
                column: "InvestigationId");

            migrationBuilder.AddForeignKey(
                name: "FK_AnomalyEvents_Investigations_InvestigationId",
                table: "AnomalyEvents",
                column: "InvestigationId",
                principalTable: "Investigations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_WarRooms_Investigations_InvestigationId",
                table: "WarRooms",
                column: "InvestigationId",
                principalTable: "Investigations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AnomalyEvents_Investigations_InvestigationId",
                table: "AnomalyEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_WarRooms_Investigations_InvestigationId",
                table: "WarRooms");

            migrationBuilder.DropIndex(
                name: "IX_WarRooms_InvestigationId",
                table: "WarRooms");

            migrationBuilder.DropIndex(
                name: "IX_AnomalyEvents_InvestigationId",
                table: "AnomalyEvents");

            migrationBuilder.DropColumn(
                name: "InvestigationId",
                table: "WarRooms");

            migrationBuilder.DropColumn(
                name: "InvestigationId",
                table: "AnomalyEvents");
        }
    }
}
