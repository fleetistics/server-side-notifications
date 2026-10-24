using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace exs.notifications_database.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationTransportType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "TransportType",
                table: "notification",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.AlterColumn<int>(
                name: "UserId",
                table: "notification_outbox",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "NotificationId",
                table: "notification_outbox",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Empty by design, as in the earlier migrations: undoing this is a deliberate manual DDL exercise. Making the
            // outbox columns NOT NULL again would fail on the first free-form email, which has neither value.
        }
    }
}
