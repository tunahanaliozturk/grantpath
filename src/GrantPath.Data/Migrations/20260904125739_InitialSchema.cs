using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GrantPath.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "abac_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    effect = table.Column<int>(type: "integer", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    condition_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_abac_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "authz_decisions",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    resource = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    relation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    decision = table.Column<int>(type: "integer", nullable: false),
                    relationship_allowed = table.Column<bool>(type: "boolean", nullable: false),
                    attribute_verdict = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason_json = table.Column<string>(type: "jsonb", nullable: false),
                    model_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    latency_ms = table.Column<double>(type: "double precision", nullable: false),
                    written_synchronously = table.Column<bool>(type: "boolean", nullable: false),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_authz_decisions", x => x.request_id);
                });

            migrationBuilder.CreateTable(
                name: "changelog_cursor",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    continuation_token = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_changelog_cursor", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document_permission_cache",
                columns: table => new
                {
                    document_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    relation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    computed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_permission_cache", x => new { x.document_id, x.subject, x.relation });
                });

            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    folder_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    title = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    classification = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_abac_policies_resource_type",
                table: "abac_policies",
                column: "resource_type");

            migrationBuilder.CreateIndex(
                name: "ix_authz_decisions_resource_decided_at_utc",
                table: "authz_decisions",
                columns: new[] { "resource", "decided_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_authz_decisions_subject_decided_at_utc",
                table: "authz_decisions",
                columns: new[] { "subject", "decided_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_documents_folder_id",
                table: "documents",
                column: "folder_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "abac_policies");

            migrationBuilder.DropTable(
                name: "authz_decisions");

            migrationBuilder.DropTable(
                name: "changelog_cursor");

            migrationBuilder.DropTable(
                name: "document_permission_cache");

            migrationBuilder.DropTable(
                name: "documents");
        }
    }
}
