using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ReelGuo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase4Hardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "age_rules",
                columns: table => new
                {
                    country_code = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_age_rules", x => x.country_code);
                });

            migrationBuilder.CreateTable(
                name: "playback_samples",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    session_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    series_id = table.Column<long>(type: "bigint", nullable: false),
                    episode_number = table.Column<int>(type: "integer", nullable: false),
                    country = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                    device = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    browser = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    startup_ms = table.Column<int>(type: "integer", nullable: true),
                    rebuffer_count = table.Column<int>(type: "integer", nullable: false),
                    rebuffer_ms = table.Column<int>(type: "integer", nullable: false),
                    watched_seconds = table.Column<double>(type: "double precision", nullable: false),
                    max_height = table.Column<int>(type: "integer", nullable: true),
                    error = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_playback_samples", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "worker_heartbeats",
                columns: table => new
                {
                    worker_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    jobs_succeeded = table.Column<long>(type: "bigint", nullable: false),
                    jobs_failed = table.Column<long>(type: "bigint", nullable: false),
                    current_job_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_worker_heartbeats", x => x.worker_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_playback_samples_at",
                table: "playback_samples",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_playback_samples_series_id_at",
                table: "playback_samples",
                columns: new[] { "series_id", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "age_rules");

            migrationBuilder.DropTable(
                name: "playback_samples");

            migrationBuilder.DropTable(
                name: "worker_heartbeats");
        }
    }
}
