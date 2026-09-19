namespace exs.notifications_model.Notifications
{
	public class NotificationTransportType
	{
		public const byte FCM = 1;

		public byte Id { get; set; }
		public string Name { get; set; } = default!;
		public DateTime LatestUpdate { get; set; }
	}
}
