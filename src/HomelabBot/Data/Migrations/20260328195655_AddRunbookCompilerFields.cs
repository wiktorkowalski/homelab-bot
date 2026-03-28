using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomelabBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRunbookCompilerFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ParentRunbookId",
                table: "Runbooks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceInvestigationId",
                table: "Runbooks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceType",
                table: "Runbooks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ParentRunbookId",
                table: "Runbooks");

            migrationBuilder.DropColumn(
                name: "SourceInvestigationId",
                table: "Runbooks");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "Runbooks");
        }
    }
}
