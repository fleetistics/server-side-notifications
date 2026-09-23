using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace exs.notifications_database.Migrations
{
    /// <summary>
    /// Two columns fcm_sent was missing to be useful as a delivery log rather than just an audit trail.
    ///
    /// ErrorCode stores the reason as a stable token - a FirebaseAdmin MessagingErrorCode name, or one
    /// of FcmSentErrorCode for the states this pipeline decides itself. Error already held the reason,
    /// but as free-form English straight from Firebase, so nothing could group on it: no failure-rate
    /// breakdown, and no way to write a cleanup job that finds sessions rejected as Unregistered
    /// without matching on prose.
    ///
    /// ProviderMessageId stores what FCM returns on success. It was already being computed and thrown
    /// away, and it is the only handle for correlating a row here against Firebase's own delivery
    /// reporting - which is the first thing wanted whenever someone reports a notification that never
    /// arrived.
    ///
    /// Both nullable, so there is nothing to backfill: rows written before this ran simply have no code
    /// recorded, which is the truth about them.
    /// </summary>
    public partial class AddFcmSentErrorCodeAndProviderMessageId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ErrorCode",
                table: "fcm_sent",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderMessageId",
                table: "fcm_sent",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Empty by design, as in InitialFcmTransport: NoDropMigrationsModelDiffer strips DropColumn
            // from the diff in both directions, so dropping these is a deliberate manual DDL exercise.
        }
    }
}
