using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActionLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActionLogEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    ActorUserId = table.Column<long>(type: "bigint", nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<int>(type: "integer", nullable: false),
                    TargetType = table.Column<int>(type: "integer", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Summary = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DetailsJson = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionLogEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActionLogEntries_CreatedAt",
                table: "ActionLogEntries",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ActionLogEntries_GuildId_CreatedAt",
                table: "ActionLogEntries",
                columns: new[] { "GuildId", "CreatedAt" },
                descending: new[] { false, true });

            // Back the CSV export's two keyset walks (events by guild+id, RSVPs by event+id).
            migrationBuilder.CreateIndex(
                name: "IX_Events_GuildId_Id",
                table: "Events",
                columns: new[] { "GuildId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Rsvps_EventId_Id",
                table: "Rsvps",
                columns: new[] { "EventId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Rsvps_EventId_Id",
                table: "Rsvps");

            migrationBuilder.DropIndex(
                name: "IX_Events_GuildId_Id",
                table: "Events");

            migrationBuilder.DropTable(
                name: "ActionLogEntries");
        }
    }
}
