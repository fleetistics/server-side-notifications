namespace exs.notifications_service.Interfaces
{
	/// <summary>
	/// A free-form email for INotificationSender.CreateEmail: one message to one address, written straight to the
	/// outbox without a notification row or a user. For mail that is not about a notification (a password reset, an
	/// invitation), whose address and link must not be kept in the notification log.
	/// </summary>
	public sealed record EmailMessage : EmailEnvelope
	{
		/// <summary>The subject line. One line: no line breaks.</summary>
		public required string Subject { get; init; }

		/// <summary>The plain-text part. At least one of Text and Html is required; with both, the mail is multipart/alternative.</summary>
		public string? Text { get; init; }

		/// <summary>The HTML part. Sent as given: nothing is sanitized or inlined.</summary>
		public string? Html { get; init; }
	}
}
