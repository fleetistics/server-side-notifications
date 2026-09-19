using exs.notifications_model.Notifications;

namespace exs.notifications_model.Transports
{
	public class FcmQueue
	{
		public int Id { get; set; }
		public DateTime Date { get; set; }
		public int NotificationId { get; set; }
		public int UserId { get; set; }
		public int UserSessionId { get; set; }
		public string? FcmToken { get; set; }
		public bool IsProcessing { get; set; }

		public Notification? Notification { get; set; }
	}
}
