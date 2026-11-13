using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace exs.notifications_database.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The scaffolder also emitted user_session.Token_IsPersistent here: that table is owned by the
            // api-server's migrations (see the note in NotificationsModelBuilder), so it is left out on purpose.
            // The snapshot does include the column, so the next migration will not try to add it either.

            migrationBuilder.CreateTable(
                name: "notification_template",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TemplateKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: true),
                    Html = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_template", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_notification_template_active",
                table: "notification_template",
                columns: new[] { "TemplateKey", "Language" },
                unique: true,
                filter: "\"IsActive\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "UX_notification_template_Key_Language_Version",
                table: "notification_template",
                columns: new[] { "TemplateKey", "Language", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
