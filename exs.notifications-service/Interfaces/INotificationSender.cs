using exs.Database.Commons.Interfaces;
using exs.notifications_service.Templates;

namespace exs.notifications_service.Interfaces
{
	/// <summary>
	/// One method per channel. Everything is written into the caller's repository, so it commits or rolls back with
	/// the caller's own changes; the work is handed to the delivery pipeline when the scope this is resolved from is
	/// disposed, and only for what the caller saved.
	/// </summary>
	public interface INotificationSender
	{
		/// <summary>A push notification (title and body) to the mobile devices of these users. Goes out as a push only.</summary>
		void CreateMobileNotification(IRepository repository, short notificationType, int entityId, string title, string body, string payload, IEnumerable<int> userIds);

		/// <summary>A data-only push (payload, no title or body) to the mobile devices of these users. Goes out as a push only.</summary>
		void CreateMobileDataNotification(IRepository repository, short notificationType, int entityId, string payload, IEnumerable<int> userIds);

		/// <summary>
		/// An email about a notification (title as the subject, body as the text, unless the host registered its own
		/// IEmailContentFactory) to these users, at the addresses the host's IEmailDestinationResolver gives. Goes out
		/// as an email only. Needs NotificationService:EmailTransport to be "Sender": otherwise the notification is
		/// marked Expired, with a warning in the log, instead of waiting for a transport that is not there.
		/// </summary>
		void CreateEmailNotification(IRepository repository, short notificationType, int entityId, string title, string body, string payload, IEnumerable<int> userIds);

		/// <summary>
		/// A free-form email to one address. Writes no notification and names no user: only an outbox message, whose
		/// content is cleared once the email sender has accepted it. The message is checked here, and this throws
		/// ArgumentException when it is not valid, so the caller's transaction fails visibly.
		/// </summary>
		/// <exception cref="InvalidOperationException">NotificationService:EmailTransport is not "Sender".</exception>
		void CreateEmail(IRepository repository, EmailMessage message);

		/// <summary>
		/// A free-form email whose subject, text and HTML come from the active template of <typeparamref name="TModel"/>
		/// (see <see cref="TemplateProcessor"/>), filled in here, before anything is written, from the data the caller has at
		/// hand. Otherwise exactly like <see cref="CreateEmail"/>: one outbox message in the caller's repository, no
		/// notification and no user. Async because the template is read from the database.
		/// <paramref name="language"/> is the language wanted, such as "uk" or "pt-BR", usually the recipient's preferred one. Null, or one with
		/// no template, gives the default language.
		/// </summary>
		/// <exception cref="InvalidOperationException">NotificationService:EmailTransport is not "Sender", or AddNotificationTemplates was not called.</exception>
		/// <exception cref="TemplateNotFoundException">No active template for the model's key.</exception>
		/// <exception cref="TemplateModelException">The model has no value for something it declares as required.</exception>
		/// <exception cref="TemplateRenderException">The template is wrong: it does not parse, reads a value the model does not have, or has unbalanced secure tags.</exception>
		/// <exception cref="ArgumentException">The rendered message is not valid, for example a subject that came out with a line break.</exception>
		Task CreateEmailFromTemplateAsync<TModel>(IRepository repository, EmailEnvelope email, TModel model, string? language = null, CancellationToken cancellationToken = default) where TModel : class, ITemplateModel;

		/// <summary>
		/// A push notification whose title and body come from the active template of <typeparamref name="TModel"/>: the
		/// template's Subject is the title and its Text the body (its Html is not used). Each recipient gets the text in their
		/// own language, so one notification row is written for every language that came out of the templates, with the users
		/// who share it. A user listed twice is notified once, in the language of their first entry. Otherwise like
		/// <see cref="CreateMobileNotification"/>, and async because the templates are read from the database. Everything is
		/// rendered before anything is written, so a failure leaves nothing behind.
		///
		/// The notification row is kept as written, so a push template must not use `&lt;secure&gt;` tags: they are refused,
		/// because masking the row would blank the push itself.
		/// </summary>
		/// <exception cref="ArgumentException">There are no recipients.</exception>
		/// <exception cref="InvalidOperationException">AddNotificationTemplates was not called.</exception>
		/// <exception cref="TemplateNotFoundException">No active template for the model's key.</exception>
		/// <exception cref="TemplateModelException">The model has no value for something it declares as required.</exception>
		/// <exception cref="TemplateRenderException">The template is wrong, or uses secure tags.</exception>
		Task CreateMobileNotificationFromTemplateAsync<TModel>(IRepository repository, short notificationType, int entityId, TModel model, string payload, IEnumerable<TemplateRecipient> recipients, CancellationToken cancellationToken = default) where TModel : class, ITemplateModel;
	}
}
