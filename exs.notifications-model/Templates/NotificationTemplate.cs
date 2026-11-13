namespace exs.notifications_model.Templates
{
	/// <summary>
	/// The wording of one message for one language, as Fluid (Liquid) text. A template is identified by its key
	/// (what the host's model class says it is, see ITemplateModel) and a language; every change is a new Version,
	/// and exactly one version per key and language is active, so wording can be changed, and changed back, without
	/// touching rows that were already used.
	///
	/// Subject and Text serve every channel: an email uses all three parts, a push notification uses Subject as the
	/// title and Text as the body. Html is only for email.
	///
	/// A part may mark what must not be kept after the message has been sent with `&lt;secure&gt;...&lt;/secure&gt;`;
	/// the email sender strips the tags before sending and stores the content as XXXX.
	/// </summary>
	public class NotificationTemplate
	{
		public long Id { get; set; }

		/// <summary>What the host's model class calls this template, for example "password-reset".</summary>
		public string TemplateKey { get; set; } = "";

		/// <summary>Lower-case language tag: "en", "uk" or a regional one such as "pt-br".</summary>
		public string Language { get; set; } = "";

		/// <summary>Starts at 1 and goes up with every edit of the same key and language.</summary>
		public int Version { get; set; } = 1;

		/// <summary>True for the version that is used. At most one row per key and language has it.</summary>
		public bool IsActive { get; set; }

		public string Subject { get; set; } = "";

		public string? Text { get; set; }

		public string? Html { get; set; }

		public DateTime CreatedAt { get; set; }

		/// <summary>Who made this version, for the audit trail. Free text: a login, or "seed".</summary>
		public string? CreatedBy { get; set; }
	}
}
