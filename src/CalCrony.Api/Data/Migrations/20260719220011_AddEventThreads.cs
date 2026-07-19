using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventThreads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WantsThread",
                table: "EventSeries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "ThreadId",
                table: "Events",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WantsThread",
                table: "Events",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WantsThread",
                table: "EventSeries");

            migrationBuilder.DropColumn(
                name: "ThreadId",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "WantsThread",
                table: "Events");
        }
    }
}
