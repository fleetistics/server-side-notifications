namespace exs.notifications_contracts
{
	/// <summary>
	/// Which template a message was written from, for a host that renders its messages from templates. Carried in the
	/// envelope so the sender can keep it next to the (masked) message: "what happened with this email" is then answered together
	/// with "which template and which wording was it". The sender does nothing else with it.
	/// </summary>
	public sealed record TemplateReference
	{
		/// <summary>The template's key in the host's notification_template table, such as "password-reset". At most <see cref="MaxKeyLength"/> characters.</summary>
		public required string Key { get; init; }

		/// <summary>The version of the template that was used, from 1.</summary>
		public required int Version { get; init; }

		/// <summary>The language of the template that was used (which can be the default one, not the one asked for). At most <see cref="MaxLanguageLength"/> characters.</summary>
		public required string Language { get; init; }

		public const int MaxKeyLength = 128;

		public const int MaxLanguageLength = 16;
	}
}
