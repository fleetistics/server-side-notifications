namespace exs.notifications_contracts
{
	/// <summary>
	/// An email to one recipient. One recipient per message on purpose: the result then says what happened
	/// to that address, and a refused address (DestinationInvalid) is unambiguous. A host that mails ten people
	/// sends ten messages. Who the mail is from is not in the body; it belongs to the account the message is
	/// sent as (the sender's configuration), which is how different hosts or apps send from different addresses.
	/// </summary>
	public sealed record EmailBody
	{
		public required EmailAddress To { get; init; }

		/// <summary>Where a reply goes, when that is not the account's From address.</summary>
		public EmailAddress? ReplyTo { get; init; }

		public required string Subject { get; init; }

		/// <summary>Plain-text part. At least one of Text and Html is required; with both, the mail is multipart/alternative.</summary>
		public string? Text { get; init; }

		/// <summary>HTML part. Sent as given: the sender does not sanitize or inline anything.</summary>
		public string? Html { get; init; }

		/// <summary>See MessagePriorities. Absent means normal.</summary>
		public string? Priority { get; init; }
	}

	public sealed record EmailAddress
	{
		/// <summary>A bare address such as sam@example.com, without a display name or angle brackets.</summary>
		public required string Address { get; init; }

		/// <summary>Display name shown by mail clients.</summary>
		public string? Name { get; init; }
	}
}
