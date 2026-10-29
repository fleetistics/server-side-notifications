namespace exs.notifications_contracts
{
	/// <summary>
	/// What a host hands to a sender. Channel-agnostic header plus a channel-specific body; the body type
	/// is chosen by Channel (FcmBody for "fcm"). A receiver that does not yet know the channel reads the
	/// header with MessageEnvelope&lt;JsonElement&gt; and decodes the body once it does.
	///
	/// Nothing here identifies a user. The host resolves users to destinations before it gets this far, and
	/// the sender never needs to know who they are.
	/// </summary>
	public sealed record MessageEnvelope<TBody>
	{
		public required int SchemaVersion { get; init; }

		/// <summary>Host-generated idempotency key (the outbox row id). A sender dedupes on (client, MessageId).</summary>
		public required Guid MessageId { get; init; }

		public required string Channel { get; init; }

		/// <summary>
		/// Alias of the provider account to send as (which Firebase project, which SMTP login). The alias is
		/// resolved to credentials on the sender side only, which also checks the calling client may use it.
		/// Omitted means that client's default account for the channel.
		/// </summary>
		public string? Account { get; init; }

		public required DateTimeOffset CreatedAt { get; init; }

		/// <summary>Absolute deadline: a sender that has not sent by now reports the message as expired.</summary>
		public required DateTimeOffset ExpiresAt { get; init; }

		/// <summary>
		/// Opaque correlation string (at most 256 characters), echoed verbatim in the result. The host decides
		/// what it carries, for example the session id; the sender never parses it.
		/// </summary>
		public string? Ref { get; init; }

		public required TBody Body { get; init; }
	}
}
