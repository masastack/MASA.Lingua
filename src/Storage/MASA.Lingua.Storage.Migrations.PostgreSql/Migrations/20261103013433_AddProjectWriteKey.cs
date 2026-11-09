using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MASA.Lingua.Storage.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectWriteKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "write_key",
                table: "projects",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "write_key",
                table: "projects");
        }
    }
}
