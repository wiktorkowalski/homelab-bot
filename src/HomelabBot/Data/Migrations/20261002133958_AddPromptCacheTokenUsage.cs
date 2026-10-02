using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomelabBot.Data.Migrations;

/// <inheritdoc />
public partial class AddPromptCacheTokenUsage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "CacheWriteTokens",
            table: "LlmInteractions",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "CachedPromptTokens",
            table: "LlmInteractions",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "LlmRounds",
            table: "LlmInteractions",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CacheWriteTokens",
            table: "LlmInteractions");

        migrationBuilder.DropColumn(
            name: "CachedPromptTokens",
            table: "LlmInteractions");

        migrationBuilder.DropColumn(
            name: "LlmRounds",
            table: "LlmInteractions");
    }
}
