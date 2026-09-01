using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RecipientUserId",
                table: "Deliveries",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_RecipientUserId",
                table: "Deliveries",
                column: "RecipientUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Deliveries_RecipientUserId",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "RecipientUserId",
                table: "Deliveries");
        }
    }
}
