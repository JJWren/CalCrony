using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublicCalendarSlug",
                table: "Guilds",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Guilds_PublicCalendarSlug",
                table: "Guilds",
                column: "PublicCalendarSlug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Guilds_PublicCalendarSlug",
                table: "Guilds");

            migrationBuilder.DropColumn(
                name: "PublicCalendarSlug",
                table: "Guilds");
        }
    }
}
