using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    CreatorId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: true),
                    Location = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ImageUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    RecurrenceUnit = table.Column<int>(type: "integer", nullable: true),
                    RecurrenceInterval = table.Column<int>(type: "integer", nullable: true),
                    RecurrenceMonthlyMode = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EventTemplateNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    MinutesBefore = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Mentions = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ChannelId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventTemplateNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventTemplateNotifications_EventTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "EventTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventTemplateNotifications_TemplateId",
                table: "EventTemplateNotifications",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_EventTemplates_GuildId_Name",
                table: "EventTemplates",
                columns: new[] { "GuildId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventTemplateNotifications");

            migrationBuilder.DropTable(
                name: "EventTemplates");
        }
    }
}
