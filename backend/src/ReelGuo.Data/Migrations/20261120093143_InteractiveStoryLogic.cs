using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelGuo.Data.Migrations
{
    /// <inheritdoc />
    public partial class InteractiveStoryLogic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "conditions_json",
                table: "story_options",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "effects_json",
                table: "story_options",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "story_layout_json",
                table: "series",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "story_variables_json",
                table: "series",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<int>(
                name: "custom_price",
                table: "episodes",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "conditions_json",
                table: "story_options");

            migrationBuilder.DropColumn(
                name: "effects_json",
                table: "story_options");

            migrationBuilder.DropColumn(
                name: "story_layout_json",
                table: "series");

            migrationBuilder.DropColumn(
                name: "story_variables_json",
                table: "series");

            migrationBuilder.DropColumn(
                name: "custom_price",
                table: "episodes");
        }
    }
}
