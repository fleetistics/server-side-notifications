namespace exs.notifications_model.Notifications
{
	public class NotificationToUser
	{
		public int UserId { get; set; }
		public int NotificationId { get; set; }
		public Notification? Notification { get; set; }
	}
}
