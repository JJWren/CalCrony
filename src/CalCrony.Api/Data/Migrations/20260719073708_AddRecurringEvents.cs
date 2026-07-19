using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecurringEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SeriesNotificationId",
                table: "EventNotifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EventSeries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    CreatorId = table.Column<long>(type: "bigint", nullable: false),
                    Unit = table.Column<int>(type: "integer", nullable: false),
                    Interval = table.Column<int>(type: "integer", nullable: false),
                    MonthlyMode = table.Column<int>(type: "integer", nullable: false),
                    AnchorDate = table.Column<LocalDate>(type: "date", nullable: false),
                    StartTime = table.Column<LocalTime>(type: "time", nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UntilDate = table.Column<LocalDate>(type: "date", nullable: true),
                    MaxOccurrences = table.Column<int>(type: "integer", nullable: true),
                    CurrentOccurrenceDate = table.Column<LocalDate>(type: "date", nullable: false),
                    OccurrenceCount = table.Column<int>(type: "integer", nullable: false),
                    Ended = table.Column<bool>(type: "boolean", nullable: false),
                    Title = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: true),
                    ChannelId = table.Column<long>(type: "bigint", nullable: false),
                    Location = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ImageUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventSeries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SeriesNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SeriesId = table.Column<Guid>(type: "uuid", nullable: false),
                    MinutesBefore = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Mentions = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ChannelId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeriesNotifications_EventSeries_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "EventSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_SeriesId",
                table: "Events",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_SeriesId_Live",
                table: "Events",
                column: "SeriesId",
                unique: true,
                filter: "\"Status\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_EventSeries_GuildId",
                table: "EventSeries",
                column: "GuildId");

            migrationBuilder.CreateIndex(
                name: "IX_SeriesNotifications_SeriesId",
                table: "SeriesNotifications",
                column: "SeriesId");

            migrationBuilder.AddForeignKey(
                name: "FK_Events_EventSeries_SeriesId",
                table: "Events",
                column: "SeriesId",
                principalTable: "EventSeries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Events_EventSeries_SeriesId",
                table: "Events");

            migrationBuilder.DropTable(
                name: "SeriesNotifications");

            migrationBuilder.DropTable(
                name: "EventSeries");

            migrationBuilder.DropIndex(
                name: "IX_Events_SeriesId",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Events_SeriesId_Live",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "SeriesNotificationId",
                table: "EventNotifications");
        }
    }
}
