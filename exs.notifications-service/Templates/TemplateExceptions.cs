namespace exs.notifications_service.Templates
{
	/// <summary>
	/// There is no active template for the model's key, in the wanted language or in the default one. A setup mistake, not
	/// something to retry: the row has to be added.
	/// </summary>
	public sealed class TemplateNotFoundException : Exception
	{
		public string TemplateKey { get; }

		public IReadOnlyList<string> LanguagesTried { get; }

		public TemplateNotFoundException(string templateKey, IReadOnlyList<string> languagesTried)
			: base($"There is no active notification template with key '{templateKey}' in language {string.Join(" or ", languagesTried.Select(l => $"'{l}'"))}. Add a row to notification_template.")
		{
			TemplateKey = templateKey;
			LanguagesTried = languagesTried;
		}
	}

	/// <summary>
	/// The model is missing values its template needs. The message names the properties, never what they hold.
	/// </summary>
	public sealed class TemplateModelException : Exception
	{
		public string TemplateKey { get; }

		/// <summary>The paths of the non-nullable properties that were null, such as "Case.User".</summary>
		public IReadOnlyList<string> MissingPaths { get; }

		public TemplateModelException(string templateKey, IReadOnlyList<string> missingPaths)
			: base($"The model for template '{templateKey}' has no value for: {string.Join(", ", missingPaths)}.")
		{
			TemplateKey = templateKey;
			MissingPaths = missingPaths;
		}
	}

	/// <summary>
	/// A template that does not parse, or that reads something its model does not have. The row is wrong, so the message
	/// says which row and part; the text of the problem comes from the template engine and holds names, not values.
	/// </summary>
	public sealed class TemplateRenderException : Exception
	{
		public string TemplateKey { get; }

		public string Language { get; }

		public int Version { get; }

		/// <summary>"Subject", "Text" or "Html".</summary>
		public string Part { get; }

		public TemplateRenderException(string templateKey, string language, int version, string part, string problem, Exception? inner = null)
			: base($"Template '{templateKey}' ({language}, version {version}), part {part}: {problem}", inner)
		{
			TemplateKey = templateKey;
			Language = language;
			Version = version;
			Part = part;
		}
	}
}
