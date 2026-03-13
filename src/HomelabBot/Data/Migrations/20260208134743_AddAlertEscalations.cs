using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomelabBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertEscalations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlertEscalations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AlertFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    AlertName = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    PhoneCallInitiatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TwilioCallSid = table.Column<string>(type: "TEXT", nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AcknowledgementMethod = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertEscalations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertEscalations_AlertFingerprint",
                table: "AlertEscalations",
                column: "AlertFingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_AlertEscalations_Status",
                table: "AlertEscalations",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertEscalations");
        }
    }
}
