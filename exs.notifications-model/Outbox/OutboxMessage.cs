using exs.notifications_model.Notifications;

namespace exs.notifications_model.Outbox
{
	public static class OutboxState
	{
		/// <summary>Written, but the sender has not accepted it yet. The relay keeps trying until it expires.</summary>
		public const byte Pending = 0;

		/// <summary>The sender accepted it (it is stored there and will be sent). Waiting for its result.</summary>
		public const byte Submitted = 1;

		/// <summary>The sender reported a result. See ResultStatus: that may be a failure.</summary>
		public const byte Completed = 2;

		/// <summary>The sender refused the message for good (a 4xx that retrying cannot change).</summary>
		public const byte Rejected = 3;

		/// <summary>It ran out of time before the sender accepted it.</summary>
		public const byte Expired = 4;
	}

	/// <summary>Machine-readable ErrorKind values this host decides itself, as opposed to the sender's own.</summary>
	public static class OutboxErrorKind
	{
		/// <summary>The user had nowhere to send to (no active mobile session with a token), so nothing was attempted.</summary>
		public const string NoDestination = "no_destination";

		/// <summary>The notification has no form this channel can send (an email needs a subject, which a data-only notification lacks), so nothing was attempted.</summary>
		public const string NoContent = "no_content";
	}

	/// <summary>ErrorCode values this host writes itself on rows it finished without calling a sender.</summary>
	public static class OutboxErrorCode
	{
		/// <summary>The user is to be emailed but the host has no address for them.</summary>
		public const string NoEmailAddress = "NoEmailAddress";

		/// <summary>The notification could not be turned into an email.</summary>
		public const string NoEmailContent = "NoEmailContent";
	}

	/// <summary>
	/// One message for one destination of one user: the host's half of the hand-off to a standalone sender.
	/// It is the transactional outbox (written in the same save as the fan-out, so a notification is never
	/// recorded without its messages or the other way round) and, once the sender reports back, the delivery
	/// log for that user and destination.
	/// </summary>
	public class OutboxMessage
	{
		public long Id { get; set; }

		/// <summary>The idempotency key the sender deduplicates on. Generated once, here, and never changed.</summary>
		public Guid MessageId { get; set; }

		/// <summary>The notification this message was fanned out from. Null for a free-form email (INotificationSender.CreateEmail), which has no notification row.</summary>
		public int? NotificationId { get; set; }

		public Notification? Notification { get; set; }

		/// <summary>The user this message is for. Null for a free-form email, whose recipient is just an address.</summary>
		public int? UserId { get; set; }

		public string Channel { get; set; } = "";

		public string? Account { get; set; }

		/// <summary>The opaque correlation string the sender echoes back. What it holds is the host's business.</summary>
		public string? Ref { get; set; }

		/// <summary>
		/// The JSON envelope to POST while the row is Pending. Cleared as soon as the sender accepts it: it
		/// holds the destination and the text, and nothing needs them once the sender has its own copy.
		/// </summary>
		public string? Envelope { get; set; }

		public DateTime CreatedAt { get; set; }

		public DateTime ExpiresAt { get; set; }

		public byte State { get; set; } = OutboxState.Pending;

		public DateTime? SubmittedAt { get; set; }

		/// <summary>Why the last submission failed, or why the sender refused it. For diagnosis only.</summary>
		public string? SubmitError { get; set; }

		// What the sender reported. All null until State is Completed.

		public long? ResultId { get; set; }

		/// <summary>sent, failed or expired. "sent" means the provider accepted the message, not that a device received it.</summary>
		public string? ResultStatus { get; set; }

		public string? ErrorKind { get; set; }

		public string? ErrorCode { get; set; }

		public string? ErrorMessage { get; set; }

		public string? ProviderMessageId { get; set; }

		public bool DestinationInvalid { get; set; }

		public int? Attempts { get; set; }

		public DateTime? CompletedAt { get; set; }
	}
}
