namespace exs.notifications_contracts
{
	/// <summary>
	/// What a sender reports back: one terminal event per message. Individual send attempts stay internal
	/// to the sender. Results form an append-only log per client, ordered by ResultId, which a host reads
	/// with a cursor.
	/// </summary>
	public sealed record ResultEvent
	{
		public required int SchemaVersion { get; init; }

		/// <summary>Monotonic within one client's results; the host's pull cursor.</summary>
		public required long ResultId { get; init; }

		public required Guid MessageId { get; init; }

		public required string Channel { get; init; }

		/// <summary>The account the message was actually sent as.</summary>
		public string? Account { get; init; }

		/// <summary>Echoed verbatim from the envelope.</summary>
		public string? Ref { get; init; }

		/// <summary>See ResultStatuses.</summary>
		public required string Status { get; init; }

		/// <summary>The destination value that was used (the token, address or number), echoed so a host can act on it safely.</summary>
		public string? Destination { get; init; }

		/// <summary>
		/// The provider says this destination will never work again, so the host should stop using it. A host
		/// clears it only if its current value still equals Destination, so a token the client has refreshed
		/// in the meantime is left alone. Strictly narrower than a permanent error: a malformed message or a
		/// broken sender credential says nothing about the destination and must not set this.
		/// </summary>
		public bool DestinationInvalid { get; init; }

		/// <summary>See ErrorKinds. Absent on success.</summary>
		public string? ErrorKind { get; init; }

		/// <summary>Provider error code, for example the FCM MessagingErrorCode name.</summary>
		public string? ErrorCode { get; init; }

		public string? ErrorMessage { get; init; }

		public string? ProviderMessageId { get; init; }

		public required int Attempts { get; init; }

		public required DateTimeOffset CompletedAt { get; init; }
	}
}
