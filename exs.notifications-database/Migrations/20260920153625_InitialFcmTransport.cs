using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace exs.notifications_database.Migrations
{
    /// <summary>
    /// First migration for this repo. HAND-EDITED after scaffolding - see the ownership note in
    /// NotificationsModelBuilder.
    ///
    /// NotificationDatabaseContext maps seven tables but this repo owns only three things:
    /// fcm_queue, fcm_sent, and the Status column on notification. The other four
    /// (client_device_platform, notification_type, notification2user, user_session) are created and
    /// altered by server-side-base-api-server's migrations and are read-only inputs here, so the
    /// CreateTable calls the scaffolder emitted for them were removed. notification's CreateTable was
    /// likewise reduced to the single AddColumn that is genuinely new.
    ///
    /// When re-editing this file, check the column lists against the model rather than against memory:
    /// UserSessionId and FcmToken belong on fcm_sent (NotificationQueueProcessor writes them and the
    /// resume dedupe query reads them) and were the dead columns on fcm_queue. Removing them from the
    /// wrong table here builds fine and passes every test, because the suite creates its SQLite schema
    /// from the model via EnsureCreated and never executes a migration - it would only surface as a
    /// runtime failure against Postgres.
    ///
    /// Consequence to be aware of: this migration cannot bootstrap an empty database on its own. It is
    /// written to be applied to a database where the api-server's migrations have already run, which is
    /// the only topology that exists.
    /// </summary>
    public partial class InitialFcmTransport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill value is Processed (1), not New (0): every notification already in the table was
            // handled by the previous notification_queue/notification_sent pipeline. Defaulting them to
            // New would make NotificationService's startup sweep load the entire notification history
            // into memory on first run and mass-update all of it to Expired, since every row predates
            // the 30-minute EXPIRATION_INTERVAL. Processed is both truthful and a no-op for the pipeline.
            migrationBuilder.AddColumn<byte>(
                name: "Status",
                table: "notification",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)1);

            // The default above exists only to satisfy NOT NULL while backfilling existing rows. EF
            // writes Status explicitly on every insert (Notification.Status initialises to New in CLR),
            // so a lingering database-level default of 1 would silently mean "already Processed" for
            // anything inserted outside EF - exactly backwards. Drop it now the backfill has happened.
            migrationBuilder.Sql("""ALTER TABLE "notification" ALTER COLUMN "Status" DROP DEFAULT;""");

            migrationBuilder.CreateTable(
                name: "fcm_sent",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NotificationId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    UserSessionId = table.Column<int>(type: "integer", nullable: true),
                    FcmToken = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fcm_sent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "fcm_queue",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NotificationId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    IsProcessing = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fcm_queue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_fcm_queue_notification_NotificationId",
                        column: x => x.NotificationId,
                        principalTable: "notification",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fcm_queue_NotificationId",
                table: "fcm_queue",
                column: "NotificationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty, and not an oversight: NoDropMigrationsModelDiffer strips every
            // DropTable/DropColumn operation out of the diff in both directions, so the scaffolder had
            // nothing to emit here. Rolling this migration back is a deliberate manual DDL exercise -
            // the estate's rule is that data under a retired table or column outlives the model.
        }
    }
}
