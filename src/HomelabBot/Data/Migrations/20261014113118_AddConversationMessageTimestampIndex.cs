using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomelabBot.Data.Migrations;

/// <inheritdoc />
public partial class AddConversationMessageTimestampIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ConversationMessages_ConversationId",
            table: "ConversationMessages");

        migrationBuilder.CreateIndex(
            name: "IX_ConversationMessages_ConversationId_Timestamp",
            table: "ConversationMessages",
            columns: new[] { "ConversationId", "Timestamp" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ConversationMessages_ConversationId_Timestamp",
            table: "ConversationMessages");

        migrationBuilder.CreateIndex(
            name: "IX_ConversationMessages_ConversationId",
            table: "ConversationMessages",
            column: "ConversationId");
    }
}
