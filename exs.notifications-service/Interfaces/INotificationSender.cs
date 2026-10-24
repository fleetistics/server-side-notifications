using exs.Database.Commons.Interfaces;

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
	}
}
