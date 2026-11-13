namespace exs.notifications_service.Templates
{
	/// <summary>A user who is to receive a templated push, and the language to write it in.</summary>
	/// <param name="UserId">The user's id.</param>
	/// <param name="Language">The language wanted, such as "uk" or "pt-BR", usually the user's preferred one. Null, or one with no template, gives the default language.</param>
	public readonly record struct TemplateRecipient(int UserId, string? Language = null);
}
