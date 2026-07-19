using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendeeRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AttendeeRoleId",
                table: "EventSeries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AttendeeRoleId",
                table: "Events",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttendeeRoleId",
                table: "EventSeries");

            migrationBuilder.DropColumn(
                name: "AttendeeRoleId",
                table: "Events");
        }
    }
}
