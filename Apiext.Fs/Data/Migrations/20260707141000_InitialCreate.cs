using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Apiext.Fs.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "background_job_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    job_type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    started_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    finished_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_by_user_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    created_by_username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    target_bucket = table.Column<string>(type: "TEXT", maxLength: 63, nullable: true),
                    parameters_json = table.Column<string>(type: "TEXT", nullable: false),
                    error = table.Column<string>(type: "TEXT", nullable: true),
                    total_items = table.Column<int>(type: "INTEGER", nullable: false),
                    processed_items = table.Column<int>(type: "INTEGER", nullable: false),
                    succeeded_items = table.Column<int>(type: "INTEGER", nullable: false),
                    skipped_items = table.Column<int>(type: "INTEGER", nullable: false),
                    failed_items = table.Column<int>(type: "INTEGER", nullable: false),
                    bytes_processed = table.Column<long>(type: "INTEGER", nullable: false),
                    stats_json = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_background_job_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bucket_policies",
                columns: table => new
                {
                    bucket_name = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    access_policy = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bucket_policies", x => x.bucket_name);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    password = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    is_super_admin = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_acls",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    bucket_name = table.Column<string>(type: "TEXT", maxLength: 63, nullable: true),
                    can_read = table.Column<bool>(type: "INTEGER", nullable: false),
                    can_add = table.Column<bool>(type: "INTEGER", nullable: false),
                    can_update = table.Column<bool>(type: "INTEGER", nullable: false),
                    can_delete = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_acls", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_acls_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_background_job_runs_created_utc",
                table: "background_job_runs",
                column: "created_utc");

            migrationBuilder.CreateIndex(
                name: "IX_background_job_runs_job_type_status",
                table: "background_job_runs",
                columns: new[] { "job_type", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_user_acls_user_id_bucket_name",
                table: "user_acls",
                columns: new[] { "user_id", "bucket_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_username",
                table: "users",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "background_job_runs");

            migrationBuilder.DropTable(
                name: "bucket_policies");

            migrationBuilder.DropTable(
                name: "user_acls");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
