using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomelabBot.Data.Migrations;

/// <inheritdoc />
public partial class AddWarRooms : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "WarRooms",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DiscordThreadId = table.Column<ulong>(type: "INTEGER", nullable: false),
                StatusMessageId = table.Column<ulong>(type: "INTEGER", nullable: false),
                Trigger = table.Column<string>(type: "TEXT", nullable: false),
                Severity = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                InvestigationId = table.Column<int>(type: "INTEGER", nullable: true),
                HealingChainId = table.Column<int>(type: "INTEGER", nullable: true),
                TimelineJson = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                Resolution = table.Column<string>(type: "TEXT", nullable: true),
                PostMortemSummary = table.Column<string>(type: "TEXT", nullable: true),
                Mttr = table.Column<TimeSpan>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WarRooms", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_WarRooms_DiscordThreadId",
            table: "WarRooms",
            column: "DiscordThreadId");

        migrationBuilder.CreateIndex(
            name: "IX_WarRooms_Status",
            table: "WarRooms",
            column: "Status");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "WarRooms");
    }
}
