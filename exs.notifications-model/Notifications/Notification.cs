namespace exs.notifications_model.Notifications
{
	public static class NotificationStatus
	{
		public const byte New = 0;
		public const byte Processed = 1;
		public const byte Expired = 2;
	}

	public class Notification
	{
		public int Id { get; set; }
		public DateTime Date { get; set; }
		public short TypeId { get; set; }
		public int? EntityId { get; set; }
		public string? Title { get; set; }
		public string? Body { get; set; }
		public string? Payload { get; set; }
		public byte Status { get; set; } = NotificationStatus.New;

		/// <summary>
		/// The one channel this notification goes out on, one of NotificationTransportType: FCM (push) or Email.
		/// Kept on the row because the periodic sweep rebuilds its work from this table alone. Defaults to FCM, in the
		/// database too, so rows that predate the column and writers that never set it mean push.
		/// </summary>
		public byte TransportType { get; set; } = NotificationTransportType.FCM;
	}
}
