using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace exs.notifications_database.Migrations
{
    /// <summary>
    /// Indexes for the three queries this pipeline runs on a loop. See NotificationsModelBuilder for
    /// why each one is shaped the way it is; this note is about applying them.
    ///
    /// Kept separate from InitialFcmTransport deliberately, so the two can be applied at different
    /// times. These are plain CREATE INDEX statements, so each takes an ACCESS EXCLUSIVE lock on its
    /// table while it builds and writes to that table block until it finishes. fcm_queue is small by
    /// construction (rows are deleted once processed) and fcm_sent starts empty, but notification is an
    /// append-only log that has been accumulating since the api-server's InitialCreate, and the build
    /// has to scan all of it even though the finished partial index only holds the unprocessed rows.
    ///
    /// If that table is big enough for the lock to matter, apply InitialFcmTransport on its own and
    /// create these out of band with CREATE INDEX CONCURRENTLY (which takes no exclusive lock but
    /// cannot run inside a transaction, so it can't live in an EF migration as written), then insert
    /// this migration's row into __EFMigrationsHistory_Notifications by hand to baseline it. Note that
    /// a failed CONCURRENTLY build leaves an INVALID index behind that has to be dropped manually.
    ///
    /// Unlike InitialFcmTransport, Down() is populated here: NoDropMigrationsModelDiffer strips
    /// DropTable and DropColumn operations but not DropIndex, so this migration is reversible.
    /// </summary>
    public partial class AddNotificationPipelineIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_notification_unprocessed",
                table: "notification",
                column: "Date",
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_fcm_sent_NotificationId_UserId",
                table: "fcm_sent",
                columns: new[] { "NotificationId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_fcm_queue_Date",
                table: "fcm_queue",
                column: "Date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_notification_unprocessed",
                table: "notification");

            migrationBuilder.DropIndex(
                name: "IX_fcm_sent_NotificationId_UserId",
                table: "fcm_sent");

            migrationBuilder.DropIndex(
                name: "IX_fcm_queue_Date",
                table: "fcm_queue");
        }
    }
}
