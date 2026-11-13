using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <summary>Adds a member's saved available hours (free slots, unit 2): one nullable column on
    /// UserProfiles holding the week as short JSON. Additive — every existing row reads as
    /// "nothing saved", so the default hours go on applying, and an older image never names the
    /// column, so rolling the IMAGE back needs no Down. Down drops the column, and with it every
    /// member's saved hours.</summary>
    /// <inheritdoc />
    public partial class AddAvailableHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvailableHoursJson",
                table: "UserProfiles",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvailableHoursJson",
                table: "UserProfiles");
        }
    }
}
