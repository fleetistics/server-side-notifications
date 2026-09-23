namespace exs.notifications_model.Transports
{
	/// <summary>
	/// Machine-readable values for <see cref="FcmSent.ErrorCode"/> that this pipeline decides itself,
	/// as opposed to the ones it passes through from FCM (the names of FirebaseAdmin's
	/// MessagingErrorCode: Unregistered, InvalidArgument, Unavailable, and so on). The two sets do not
	/// overlap, so the column can be grouped on directly to get a delivery-failure breakdown.
	/// </summary>
	public static class FcmSentErrorCode
	{
		/// <summary>The user had no active mobile session carrying a token, so nothing was attempted.</summary>
		public const string NoActiveSessions = "NoActiveSessions";

		/// <summary>The queue row outlived the delivery window and was dropped without being attempted.</summary>
		public const string Expired = "Expired";
	}

	public class FcmSent
	{
		public int Id { get; set; }
		public DateTime Date { get; set; }
		public int NotificationId { get; set; }
		public int UserId { get; set; }
		public int? UserSessionId { get; set; }
		public string? FcmToken { get; set; }

		/// <summary>
		/// Why this attempt ended, as a stable token rather than prose: either a FirebaseAdmin
		/// MessagingErrorCode name or one of <see cref="FcmSentErrorCode"/>. Null means it succeeded.
		/// Error carries the same thing as human-readable text, which is fine for reading a row but
		/// unusable for grouping - it is free-form English from Firebase and changes without notice.
		/// </summary>
		public string? ErrorCode { get; set; }

		/// <summary>Human-readable detail. Null when the send succeeded.</summary>
		public string? Error { get; set; }

		/// <summary>
		/// The message id FCM returned on success. The only handle for correlating a row here with
		/// Firebase's own delivery reporting, which is what any "the user says it never arrived"
		/// investigation needs. Null unless the send succeeded.
		/// </summary>
		public string? ProviderMessageId { get; set; }
	}
}
