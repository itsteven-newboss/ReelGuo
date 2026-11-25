using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ReelGuo.Data.Migrations
{
    /// <inheritdoc />
    public partial class InteractiveStories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "series",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                // Every existing series is an ordinary one.
                defaultValue: "Standard");

            migrationBuilder.AddColumn<long>(
                name: "story_start_episode_id",
                table: "series",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "choice_countdown",
                table: "episodes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "choice_lead_seconds",
                table: "episodes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "choice_prompt",
                table: "episodes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ending_title",
                table: "episodes",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "next_episode_id",
                table: "episodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "outcome",
                table: "episodes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unset");

            migrationBuilder.CreateTable(
                name: "story_options",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    series_id = table.Column<long>(type: "bigint", nullable: false),
                    from_episode_id = table.Column<long>(type: "bigint", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    label = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    description = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    target_episode_id = table.Column<long>(type: "bigint", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    chosen_count = table.Column<long>(type: "bigint", nullable: false),
                    timeout_count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_story_options", x => x.id);
                    table.ForeignKey(
                        name: "fk_story_options_episodes_from_episode_id",
                        column: x => x.from_episode_id,
                        principalTable: "episodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_story_options_episodes_target_episode_id",
                        column: x => x.target_episode_id,
                        principalTable: "episodes",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_story_options_series_series_id",
                        column: x => x.series_id,
                        principalTable: "series",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "story_progress",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    series_id = table.Column<long>(type: "bigint", nullable: false),
                    scene = table.Column<int>(type: "integer", nullable: false),
                    path_json = table.Column<string>(type: "jsonb", nullable: false),
                    endings = table.Column<List<int>>(type: "integer[]", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_story_progress", x => new { x.user_id, x.series_id });
                    table.ForeignKey(
                        name: "fk_story_progress_series_series_id",
                        column: x => x.series_id,
                        principalTable: "series",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_story_progress_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_episodes_next_episode_id",
                table: "episodes",
                column: "next_episode_id");

            migrationBuilder.CreateIndex(
                name: "ix_story_options_from_episode_id_sort_order",
                table: "story_options",
                columns: new[] { "from_episode_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_story_options_series_id",
                table: "story_options",
                column: "series_id");

            migrationBuilder.CreateIndex(
                name: "ix_story_options_target_episode_id",
                table: "story_options",
                column: "target_episode_id");

            migrationBuilder.CreateIndex(
                name: "ix_story_progress_series_id_scene",
                table: "story_progress",
                columns: new[] { "series_id", "scene" });

            migrationBuilder.AddForeignKey(
                name: "fk_episodes_episodes_next_episode_id",
                table: "episodes",
                column: "next_episode_id",
                principalTable: "episodes",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_episodes_episodes_next_episode_id",
                table: "episodes");

            migrationBuilder.DropTable(
                name: "story_options");

            migrationBuilder.DropTable(
                name: "story_progress");

            migrationBuilder.DropIndex(
                name: "ix_episodes_next_episode_id",
                table: "episodes");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "series");

            migrationBuilder.DropColumn(
                name: "story_start_episode_id",
                table: "series");

            migrationBuilder.DropColumn(
                name: "choice_countdown",
                table: "episodes");

            migrationBuilder.DropColumn(
                name: "choice_lead_seconds",
                table: "episodes");

            migrationBuilder.DropColumn(
                name: "choice_prompt",
                table: "episodes");

            migrationBuilder.DropColumn(
                name: "ending_title",
                table: "episodes");

            migrationBuilder.DropColumn(
                name: "next_episode_id",
                table: "episodes");

            migrationBuilder.DropColumn(
                name: "outcome",
                table: "episodes");
        }
    }
}
