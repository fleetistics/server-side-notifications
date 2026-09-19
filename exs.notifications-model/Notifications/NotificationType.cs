namespace exs.notifications_model.Notifications
{
	public class NotificationType
	{
		public const short Generic = 1;

		public short Id { get; set; }
		public string Name { get; set; } = default!;
		public DateTime LatestUpdate { get; set; }
	}
}
