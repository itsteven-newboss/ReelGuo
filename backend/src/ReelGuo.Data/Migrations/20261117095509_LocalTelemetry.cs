using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ReelGuo.Data.Migrations
{
    /// <inheritdoc />
    public partial class LocalTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "telemetry_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    service = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    category = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    exception = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    trace_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    span_id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telemetry_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "telemetry_spans",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    trace_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    span_id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    parent_span_id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    is_root = table.Column<bool>(type: "boolean", nullable: false),
                    service = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration_ms = table.Column<double>(type: "double precision", nullable: false),
                    is_error = table.Column<bool>(type: "boolean", nullable: false),
                    status_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    attributes = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telemetry_spans", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_logs_at",
                table: "telemetry_logs",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_logs_trace_id",
                table: "telemetry_logs",
                column: "trace_id");

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_spans_is_root_started_at",
                table: "telemetry_spans",
                columns: new[] { "is_root", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_spans_started_at",
                table: "telemetry_spans",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_spans_trace_id",
                table: "telemetry_spans",
                column: "trace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "telemetry_logs");

            migrationBuilder.DropTable(
                name: "telemetry_spans");
        }
    }
}
