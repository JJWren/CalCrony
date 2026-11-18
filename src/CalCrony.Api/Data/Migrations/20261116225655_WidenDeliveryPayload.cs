using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <summary>Takes the length limit off a delivery's payload: Deliveries.PayloadJson becomes
    /// text. A payload is bounded by the limits of the fields written into it, so the column
    /// needs none of its own. No row is rewritten, and an older image reads and writes the
    /// column as before, so rolling the IMAGE back needs no Down. Down puts the 8192-character
    /// limit back, and first deletes the deliveries whose payload would not fit under it.</summary>
    /// <inheritdoc />
    public partial class WidenDeliveryPayload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PayloadJson",
                table: "Deliveries",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(8192)",
                oldMaxLength: 8192);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The narrower column cannot hold these rows, and the change of type would stop at
            // the first of them.
            migrationBuilder.Sql("DELETE FROM \"Deliveries\" WHERE length(\"PayloadJson\") > 8192;");

            migrationBuilder.AlterColumn<string>(
                name: "PayloadJson",
                table: "Deliveries",
                type: "character varying(8192)",
                maxLength: 8192,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
