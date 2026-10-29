namespace exs.notifications_contracts
{
	// The response shapes of the HTTP transport, defined here so a host's client and a sender's endpoints
	// cannot disagree about them.

	public static class IntakeStatuses
	{
		/// <summary>The message is stored and will be sent.</summary>
		public const string Accepted = "accepted";

		/// <summary>This (client, messageId) was accepted before; nothing was stored again. Hosts treat it like Accepted.</summary>
		public const string Duplicate = "duplicate";
	}

	/// <summary>Why a message was refused at intake. Also the codes of ErrorResponse.</summary>
	public static class IntakeErrorCodes
	{
		public const string Unauthorized = "unauthorized";
		public const string UnknownClient = "unknown_client";
		public const string InvalidJson = "invalid_json";
		/// <summary>An empty messageId would make every message from that host collide, so it is refused instead of deduplicated away.</summary>
		public const string InvalidMessageId = "invalid_message_id";
		public const string UnsupportedSchemaVersion = "unsupported_schema_version";
		public const string UnknownChannel = "unknown_channel";
		public const string UnknownAccount = "unknown_account";
		public const string AccountNotAllowed = "account_not_allowed";
		public const string InvalidRef = "invalid_ref";
		public const string InvalidBody = "invalid_body";
	}

	/// <summary>Body of the 202 returned by POST /v1/messages, for both a new and a repeated message.</summary>
	public sealed record IntakeResponse
	{
		public required Guid MessageId { get; init; }

		/// <summary>See IntakeStatuses.</summary>
		public required string Status { get; init; }
	}

	public sealed record ContractError
	{
		/// <summary>See IntakeErrorCodes.</summary>
		public required string Code { get; init; }

		public required string Message { get; init; }
	}

	/// <summary>Body of a 4xx from the sender. Several problems with one message are reported together.</summary>
	public sealed record ErrorResponse
	{
		public required List<ContractError> Errors { get; init; }
	}

	/// <summary>
	/// One page of GET /v1/results. A result appears a few seconds after the message completed (the sender's
	/// Sender:ResultSettleSeconds), so that a reader never moves its cursor past a result that is still being
	/// committed; a host should expect "sent" to reach it slightly late, not instantly.
	/// </summary>
	public sealed record ResultPage
	{
		public required List<ResultEvent> Results { get; init; }

		/// <summary>Pass this as "after" on the next call. Equals the request's "after" when the page is empty.</summary>
		public required long NextAfter { get; init; }
	}
}
