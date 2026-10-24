using exs.Database.Commons.Interfaces;
using exs.notifications_model.Notifications;

namespace exs.notifications_service.Sender
{
	/// <summary>One address an email should go to: a user's, as the host knows it.</summary>
	/// <param name="Name">Display name shown by mail clients. Optional.</param>
	/// <param name="Account">Which SMTP account (so which From address) the mail is sent as. Null: the host's default account for email, or the sender's.</param>
	/// <param name="Ref">An opaque string handed back with the result, so the host can find what it needs again.</param>
	public sealed record EmailResolvedDestination(int UserId, string Address, string? Name = null, string? Account = null, string? Ref = null);

	/// <summary>
	/// The part of sending an email that only the host can know: whether a user is to be emailed about this
	/// notification and where. A standalone sender has no users, so before anything is handed over the host turns
	/// user ids into addresses.
	///
	/// Registered as a singleton and called concurrently, so an implementation holds no per-request state; it
	/// gets the repository of the fan-out's own scope on every call.
	/// </summary>
	public interface IEmailDestinationResolver
	{
		/// <summary>
		/// Returns the addresses for these users. A user who is not to be emailed, or has no address, simply does
		/// not appear in the result; the caller records that nothing could be attempted for them. This is also the
		/// place for a user's own preferences ("no email for this kind of notification").
		/// </summary>
		Task<IReadOnlyList<EmailResolvedDestination>> ResolveAsync(IRepository repository, Notification notification, IReadOnlyCollection<int> userIds, CancellationToken cancellationToken);
	}

	/// <summary>What the email says. Everything but the subject is optional, but a mail needs text, html, or both.</summary>
	/// <param name="ReplyTo">Where replies go, when that is not the account's own reply-to.</param>
	/// <param name="Priority">One of exs.notifications_contracts.MessagePriorities. Null: normal.</param>
	public sealed record EmailContent(string Subject, string? Text, string? Html = null, string? ReplyTo = null, string? Priority = null);

	/// <summary>
	/// Turns a notification into an email for one recipient. The default sends the notification's title as the
	/// subject and its body as plain text. A host that wants branded HTML, a translated subject or a template
	/// registers its own; it is also the place to use the recipient (their language, their name).
	/// </summary>
	public interface IEmailContentFactory
	{
		/// <summary>Returns the email, or null when this notification has no email form (the user is then recorded as having had nothing to send).</summary>
		EmailContent? Build(Notification notification, EmailResolvedDestination destination);
	}

	/// <summary>
	/// What to do when the sender reports an address refused for good (the mailbox does not exist). Optional:
	/// without one the bounce is only recorded on the outbox row. A host usually flags the address so it is not
	/// mailed again and the user can be asked to correct it. Called after the result has been recorded, and best
	/// effort: a failure is logged, not retried.
	/// </summary>
	public interface IEmailAddressInvalidator
	{
		/// <param name="userId">Whose address it was.</param>
		/// <param name="ref">The Ref the resolver put on the destination.</param>
		/// <param name="address">The refused address, as the sender echoed it. Act on it only where it still matches, so an address the user has corrected since is left alone.</param>
		/// <returns>Whether anything was changed.</returns>
		Task<bool> InvalidateAsync(IRepository repository, int userId, string? @ref, string address, CancellationToken cancellationToken);
	}

	/// <summary>The default content: title as subject, body as plain text. A notification without a title has no email form.</summary>
	public sealed class DefaultEmailContentFactory : IEmailContentFactory
	{
		public EmailContent? Build(Notification notification, EmailResolvedDestination destination)
		{
			if (string.IsNullOrWhiteSpace(notification.Title)) return null;
			return new EmailContent(notification.Title.Trim(), string.IsNullOrWhiteSpace(notification.Body) ? notification.Title : notification.Body);
		}
	}
}
