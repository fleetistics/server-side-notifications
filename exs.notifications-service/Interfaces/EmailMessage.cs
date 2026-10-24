namespace exs.notifications_service.Interfaces
{
	/// <summary>
	/// A free-form email for INotificationSender.CreateEmail: one message to one address, written straight to the
	/// outbox without a notification row or a user. For mail that is not about a notification (a password reset, an
	/// invitation), whose address and link must not be kept in the notification log.
	/// </summary>
	public sealed record EmailMessage
	{
		/// <summary>The recipient: one bare address such as sam@example.com, without a display name, angle brackets or a list.</summary>
		public required string To { get; init; }

		/// <summary>The recipient's display name, shown by mail clients.</summary>
		public string? ToName { get; init; }

		/// <summary>The subject line. One line: no line breaks.</summary>
		public required string Subject { get; init; }

		/// <summary>The plain-text part. At least one of Text and Html is required; with both, the mail is multipart/alternative.</summary>
		public string? Text { get; init; }

		/// <summary>The HTML part. Sent as given: nothing is sanitized or inlined.</summary>
		public string? Html { get; init; }

		/// <summary>Where a reply goes, when that is not the sending account's own address. One bare address.</summary>
		public string? ReplyTo { get; init; }

		/// <summary>The display name of <see cref="ReplyTo"/>.</summary>
		public string? ReplyToName { get; init; }

		/// <summary>Null (normal), or one of exs.notifications_contracts.MessagePriorities: "normal" or "high".</summary>
		public string? Priority { get; init; }

		/// <summary>The sender account (the From address) to send as, by the alias the email sender knows it under. Null: the email sender's DefaultAccount.</summary>
		public string? Account { get; init; }

		/// <summary>An opaque string, at most 256 characters, that comes back unchanged with the delivery result so the caller can find what it needs again.</summary>
		public string? Ref { get; init; }
	}
}
