namespace exs.notifications_service.Templates
{
	/// <summary>Configuration section "NotificationService:Templates".</summary>
	public sealed class NotificationTemplateOptions
	{
		public const string SectionName = "NotificationService:Templates";

		/// <summary>The language used when a message asks for none, or asks for one that has no template.</summary>
		public string DefaultLanguage { get; set; } = "en";
	}
}
