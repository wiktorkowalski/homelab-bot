using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomelabBot.Data.Migrations;

/// <inheritdoc />
public partial class AddAnomalyEvents : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AnomalyEvents",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Summary = table.Column<string>(type: "TEXT", nullable: false),
                Analysis = table.Column<string>(type: "TEXT", nullable: true),
                Severity = table.Column<string>(type: "TEXT", nullable: false),
                AnomalyCount = table.Column<int>(type: "INTEGER", nullable: false),
                DetectedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AnomalyEvents", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AnomalyEvents_DetectedAt",
            table: "AnomalyEvents",
            column: "DetectedAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AnomalyEvents");
    }
}
