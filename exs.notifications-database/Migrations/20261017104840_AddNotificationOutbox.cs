using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace exs.notifications_database.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_outbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Account = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Ref = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Envelope = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<byte>(type: "smallint", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SubmitError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ResultId = table.Column<long>(type: "bigint", nullable: true),
                    ResultStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ErrorKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ProviderMessageId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DestinationInvalid = table.Column<bool>(type: "boolean", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_outbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notification_outbox_notification_NotificationId",
                        column: x => x.NotificationId,
                        principalTable: "notification",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_transport_cursor",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Position = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_transport_cursor", x => x.Name);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_outbox_CompletedAt",
                table: "notification_outbox",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_notification_outbox_NotificationId_UserId",
                table: "notification_outbox",
                columns: new[] { "NotificationId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_outbox_pending",
                table: "notification_outbox",
                column: "CreatedAt",
                filter: "\"State\" = 0");

            migrationBuilder.CreateIndex(
                name: "UX_notification_outbox_MessageId",
                table: "notification_outbox",
                column: "MessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
