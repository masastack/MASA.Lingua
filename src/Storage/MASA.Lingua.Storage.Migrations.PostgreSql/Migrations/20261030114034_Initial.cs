using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MASA.Lingua.Storage.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    default_culture = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    supported_cultures = table.Column<List<string>>(type: "text[]", nullable: false),
                    shared_namespace_codes = table.Column<List<string>>(type: "text[]", nullable: false),
                    last_published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_published_version_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    publish_policy = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "glossary_terms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_term = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    do_not_translate = table.Column<bool>(type: "boolean", nullable: false),
                    case_sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    target_translations = table.Column<string>(type: "jsonb", nullable: false),
                    accepted_variants = table.Column<string>(type: "jsonb", nullable: false),
                    forbidden_translations = table.Column<string>(type: "jsonb", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_glossary_terms", x => x.id);
                    table.ForeignKey(
                        name: "FK_glossary_terms_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "publish_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    release_note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    entry_count = table.Column<int>(type: "integer", nullable: false),
                    published_cultures = table.Column<List<string>>(type: "text[]", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    rollback_of_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    forced = table.Column<bool>(type: "boolean", nullable: false),
                    force_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    gate_summary = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    content = table.Column<byte[]>(type: "bytea", nullable: true),
                    content_size = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_publish_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "FK_publish_snapshots_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_publish_snapshots_publish_snapshots_rollback_of_snapshot_id",
                        column: x => x.rollback_of_snapshot_id,
                        principalTable: "publish_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "translation_namespaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_shared = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_translation_namespaces", x => x.id);
                    table.ForeignKey(
                        name: "FK_translation_namespaces_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "translation_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    namespace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    source_text = table.Column<string>(type: "text", nullable: false),
                    context = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    max_length = table.Column<int>(type: "integer", nullable: true),
                    is_auto_reported = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_translation_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_translation_entries_translation_namespaces_namespace_id",
                        column: x => x.namespace_id,
                        principalTable: "translation_namespaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "translation_values",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    culture = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    review_comment = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    source_text_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_translation_values", x => x.id);
                    table.ForeignKey(
                        name: "FK_translation_values_translation_entries_entry_id",
                        column: x => x.entry_id,
                        principalTable: "translation_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_glossary_terms_project_id_source_term",
                table: "glossary_terms",
                columns: new[] { "project_id", "source_term" },
                unique: true,
                filter: "project_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_glossary_terms_source_term",
                table: "glossary_terms",
                column: "source_term",
                unique: true,
                filter: "project_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_projects_code",
                table: "projects",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_publish_snapshots_project_id_published_at",
                table: "publish_snapshots",
                columns: new[] { "project_id", "published_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_publish_snapshots_rollback_of_snapshot_id",
                table: "publish_snapshots",
                column: "rollback_of_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_translation_entries_namespace_id_key",
                table: "translation_entries",
                columns: new[] { "namespace_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_translation_namespaces_code",
                table: "translation_namespaces",
                column: "code",
                unique: true,
                filter: "project_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_translation_namespaces_project_id_code",
                table: "translation_namespaces",
                columns: new[] { "project_id", "code" },
                unique: true,
                filter: "project_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_translation_values_entry_id_culture",
                table: "translation_values",
                columns: new[] { "entry_id", "culture" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "glossary_terms");

            migrationBuilder.DropTable(
                name: "publish_snapshots");

            migrationBuilder.DropTable(
                name: "translation_values");

            migrationBuilder.DropTable(
                name: "translation_entries");

            migrationBuilder.DropTable(
                name: "translation_namespaces");

            migrationBuilder.DropTable(
                name: "projects");
        }
    }
}
