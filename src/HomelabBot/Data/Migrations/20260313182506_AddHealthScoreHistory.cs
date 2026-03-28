using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomelabBot.Data.Migrations;

/// <inheritdoc />
public partial class AddHealthScoreHistory : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "HealthScoreHistory",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Score = table.Column<int>(type: "INTEGER", nullable: false),
                AlertDeductions = table.Column<int>(type: "INTEGER", nullable: false),
                ContainerDeductions = table.Column<int>(type: "INTEGER", nullable: false),
                PoolDeductions = table.Column<int>(type: "INTEGER", nullable: false),
                MonitoringDeductions = table.Column<int>(type: "INTEGER", nullable: false),
                ConnectivityDeductions = table.Column<int>(type: "INTEGER", nullable: false),
                RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HealthScoreHistory", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_HealthScoreHistory_RecordedAt",
            table: "HealthScoreHistory",
            column: "RecordedAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "HealthScoreHistory");
    }
}
