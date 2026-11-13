namespace exs.notifications_service.Templates
{
	/// <summary>
	/// A template filled in. Which template it came from is kept so a sent message can say it: the key, the language that
	/// was actually used (the default one when the wanted language had no row) and the version.
	/// </summary>
	public sealed class RenderedTemplate
	{
		public required string TemplateKey { get; init; }

		public required string Language { get; init; }

		public required int Version { get; init; }

		public required string Subject { get; init; }

		/// <summary>Null when the template has no text part.</summary>
		public string? Text { get; init; }

		/// <summary>Null when the template has no HTML part.</summary>
		public string? Html { get; init; }
	}
}
