using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGuildBotPresence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BotPresent",
                table: "Guilds",
                type: "boolean",
                nullable: false,
                // Existing rows are guilds the bot has been used in — treat them as present;
                // the bot's Ready-time sync reconciles reality on its next startup.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BotPresent",
                table: "Guilds");
        }
    }
}
