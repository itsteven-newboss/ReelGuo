using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelGuo.Data.Migrations
{
    /// <inheritdoc />
    public partial class CosVideo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "video_bytes",
                table: "episodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "video_key",
                table: "episodes",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "video_bytes",
                table: "episodes");

            migrationBuilder.DropColumn(
                name: "video_key",
                table: "episodes");
        }
    }
}
