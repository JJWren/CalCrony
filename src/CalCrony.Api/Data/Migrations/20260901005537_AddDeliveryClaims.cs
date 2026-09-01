using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "ClaimedAt",
                table: "Deliveries",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "Deliveries");
        }
    }
}
