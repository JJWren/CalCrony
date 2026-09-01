using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecurrenceDaySets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RecurrenceDaysOfWeek",
                table: "EventTemplates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DaysOfWeek",
                table: "EventSeries",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecurrenceDaysOfWeek",
                table: "EventTemplates");

            migrationBuilder.DropColumn(
                name: "DaysOfWeek",
                table: "EventSeries");
        }
    }
}
