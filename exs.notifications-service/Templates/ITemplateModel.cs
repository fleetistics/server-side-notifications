namespace exs.notifications_service.Templates
{
	/// <summary>
	/// Marks a class as the data of one template: its public properties, and the properties of the classes they hold, are
	/// what the template can read, and nothing else is reachable from it. Declare one per message in the host that sends
	/// it, as a plain view model filled by the publisher (never an entity: an entity brings its lazy-loaded navigations
	/// and every column along, including ones a template must not see):
	///
	///   public sealed class ResetPasswordData : ITemplateModel
	///   {
	///       public static string TemplateKey => "password-reset";
	///       public required string UserName { get; init; }
	///       public required string ResetLink { get; init; }
	///   }
	///
	/// and the template reads {{ user_name }}. A non-nullable property that is null when the message is rendered is refused
	/// before anything is written. See <see cref="TemplateProcessor"/>.
	/// </summary>
	public interface ITemplateModel
	{
		/// <summary>The key of this model's rows in notification_template. A constant: it is read without an instance.</summary>
		static abstract string TemplateKey { get; }
	}
}
