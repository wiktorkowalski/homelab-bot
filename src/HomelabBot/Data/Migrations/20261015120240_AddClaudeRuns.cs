using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomelabBot.Data.Migrations;

/// <inheritdoc />
public partial class AddClaudeRuns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ClaudeRuns",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Origin = table.Column<int>(type: "INTEGER", nullable: false),
                Task = table.Column<string>(type: "TEXT", nullable: false),
                SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                Resume = table.Column<bool>(type: "INTEGER", nullable: false),
                ChannelId = table.Column<ulong>(type: "INTEGER", nullable: true),
                StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                NumTurns = table.Column<int>(type: "INTEGER", nullable: true),
                TotalCostUsd = table.Column<double>(type: "REAL", nullable: true),
                IsError = table.Column<bool>(type: "INTEGER", nullable: true),
                PermissionDenialsJson = table.Column<string>(type: "TEXT", nullable: true),
                State = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ClaudeRuns", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ClaudeSessionLinks",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DiscordId = table.Column<ulong>(type: "INTEGER", nullable: false),
                SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ClaudeSessionLinks", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ClaudeRuns_State_StartedAt",
            table: "ClaudeRuns",
            columns: new[] { "State", "StartedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_ClaudeSessionLinks_DiscordId",
            table: "ClaudeSessionLinks",
            column: "DiscordId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ClaudeRuns");

        migrationBuilder.DropTable(
            name: "ClaudeSessionLinks");
    }
}
