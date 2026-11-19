using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelGuo.Data.Migrations
{
    /// <inheritdoc />
    public partial class CardBadges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "card_badge",
                table: "series",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                // Series from before badges were set in admin keep the automatic badge (热播 / 新片 by rank and age).
                defaultValue: "Auto");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "card_badge",
                table: "series");
        }
    }
}
