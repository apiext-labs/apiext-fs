using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Apiext.Fs.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddObjectIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "object_index",
                columns: table => new
                {
                    bucket = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    size = table.Column<long>(type: "INTEGER", nullable: false),
                    last_write_utc_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                    etag = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    content_type = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    last_modified = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_object_index", x => new { x.bucket, x.key });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "object_index");
        }
    }
}
