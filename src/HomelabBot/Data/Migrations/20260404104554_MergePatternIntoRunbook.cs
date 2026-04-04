using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomelabBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class MergePatternIntoRunbook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "PatternId",
                table: "RemediationActions",
                newName: "RunbookId");

            migrationBuilder.AddColumn<string>(
                name: "CommonCause",
                table: "Runbooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailureCount",
                table: "Runbooks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeen",
                table: "Runbooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OccurrenceCount",
                table: "Runbooks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SuccessCount",
                table: "Runbooks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Runbooks_TriggerCondition",
                table: "Runbooks",
                column: "TriggerCondition");

            // Merge stats into Runbooks that already exist with matching TriggerCondition
            migrationBuilder.Sql("""
                UPDATE Runbooks
                SET OccurrenceCount = (SELECT MAX(OccurrenceCount) FROM Patterns WHERE Symptom = Runbooks.TriggerCondition),
                    SuccessCount = (SELECT SUM(SuccessCount) FROM Patterns WHERE Symptom = Runbooks.TriggerCondition),
                    FailureCount = (SELECT SUM(FailureCount) FROM Patterns WHERE Symptom = Runbooks.TriggerCondition),
                    CommonCause = COALESCE(Runbooks.CommonCause, (SELECT CommonCause FROM Patterns WHERE Symptom = Runbooks.TriggerCondition LIMIT 1)),
                    LastSeen = (SELECT MAX(LastSeen) FROM Patterns WHERE Symptom = Runbooks.TriggerCondition)
                WHERE TriggerCondition IN (SELECT Symptom FROM Patterns)
                """);

            // Migrate Pattern data into Runbooks (deduplicate by Symptom)
            migrationBuilder.Sql("""
                INSERT INTO Runbooks (Name, TriggerCondition, StepsJson, Enabled, Version, TrustLevel,
                    ExecutionCount, CreatedAt, SourceType, CommonCause, OccurrenceCount, SuccessCount,
                    FailureCount, LastSeen, Description)
                SELECT 'Pattern: ' || Symptom, Symptom, '[]', 1, 1, 0,
                    0, datetime('now'), 1, CommonCause, MAX(OccurrenceCount), SUM(SuccessCount),
                    SUM(FailureCount), MAX(LastSeen), Resolution
                FROM Patterns
                WHERE Symptom NOT IN (SELECT TriggerCondition FROM Runbooks)
                GROUP BY Symptom
                """);

            // Update RemediationActions to point to migrated Runbooks
            migrationBuilder.Sql("""
                UPDATE RemediationActions
                SET RunbookId = (
                    SELECT r.Id FROM Runbooks r
                    JOIN Patterns p ON r.TriggerCondition = p.Symptom
                    WHERE p.Id = RemediationActions.RunbookId
                    LIMIT 1
                )
                WHERE RunbookId IS NOT NULL
                """);

            migrationBuilder.DropTable(
                name: "Patterns");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Runbooks_TriggerCondition",
                table: "Runbooks");

            migrationBuilder.DropColumn(
                name: "CommonCause",
                table: "Runbooks");

            migrationBuilder.DropColumn(
                name: "FailureCount",
                table: "Runbooks");

            migrationBuilder.DropColumn(
                name: "LastSeen",
                table: "Runbooks");

            migrationBuilder.DropColumn(
                name: "OccurrenceCount",
                table: "Runbooks");

            migrationBuilder.DropColumn(
                name: "SuccessCount",
                table: "Runbooks");

            migrationBuilder.RenameColumn(
                name: "RunbookId",
                table: "RemediationActions",
                newName: "PatternId");

            migrationBuilder.CreateTable(
                name: "Patterns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CommonCause = table.Column<string>(type: "TEXT", nullable: true),
                    FailureCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OccurrenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Resolution = table.Column<string>(type: "TEXT", nullable: true),
                    SuccessCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Symptom = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Patterns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Patterns_Symptom",
                table: "Patterns",
                column: "Symptom");
        }
    }
}
