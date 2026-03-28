using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomelabBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ServiceStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServiceName = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceStates_ServiceName_Key",
                table: "ServiceStates",
                columns: new[] { "ServiceName", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServiceStates");
        }
    }
}
