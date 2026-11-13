namespace exs.notifications_service.Interfaces
{
	/// <summary>
	/// Who an email goes to and how it is delivered, without its wording: the part <see cref="EmailMessage"/> (wording written by the
	/// caller) and INotificationSender.CreateEmailFromTemplateAsync (wording from a template) have in common. Not the wire
	/// MessageEnvelope of exs.notifications_contracts, which the host builds from this and the wording and sends to the email sender.
	/// </summary>
	public record EmailEnvelope
	{
		/// <summary>The recipient: one bare address such as sam@example.com, without a display name, angle brackets or a list.</summary>
		public required string To { get; init; }

		/// <summary>The recipient's display name, shown by mail clients.</summary>
		public string? ToName { get; init; }

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
