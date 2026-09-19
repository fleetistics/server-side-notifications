namespace exs.notifications_model.Transports
{
	public class FcmSent
	{
		public int Id { get; set; }
		public DateTime Date { get; set; }
		public int NotificationId { get; set; }
		public int UserId { get; set; }
		public int? UserSessionId { get; set; }
		public string? FcmToken { get; set; } 
		public string? Error { get; set; }
	}
}
