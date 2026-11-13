using exs.Database.Commons.Interfaces;
using exs.notifications_contracts;
using exs.notifications_model.Notifications;
using exs.notifications_model.Outbox;
using exs.notifications_service.Interfaces;
using exs.notifications_service.Sender;
using exs.notifications_service.Templates;
using Microsoft.Extensions.Logging;

namespace exs.notifications_service.Impl
{
	public class NotificationSender : INotificationSender, IDisposable
	{
		/// <param name="outboxSignals">Present only when something goes through a standalone sender. Poked on Dispose after a free-form email, so the email relay submits it at once.</param>
		/// <param name="emailWriter">Present only when NotificationService:EmailTransport is "Sender". Without it CreateEmail has nowhere to send to and refuses.</param>
		/// <param name="templateProcessor">Present only when the host called AddNotificationTemplates. Without it CreateEmailFromTemplateAsync refuses.</param>
		public NotificationSender(INotificationService notificationService, ILogger<NotificationSender> logger, OutboxSignals? outboxSignals = null, EmailOutboxWriter? emailWriter = null, TemplateProcessor? templateProcessor = null)
		{
			mNotificationService = notificationService;
			mLogger = logger;
			mOutboxSignals = outboxSignals;
			mEmailWriter = emailWriter;
			mTemplateProcessor = templateProcessor;
		}

		public void CreateMobileNotification(IRepository repository, short notificationType, int entityId, string title, string body, string payload, IEnumerable<int> userIds)
		{
			createNotification(repository, NotificationTransportType.FCM, notificationType, entityId, title, body, payload, userIds);
		}

		public void CreateMobileDataNotification(IRepository repository, short notificationType, int entityId, string payload, IEnumerable<int> userIds)
		{
			createNotification(repository, NotificationTransportType.FCM, notificationType, entityId, null, null, payload, userIds);
		}

		public void CreateEmailNotification(IRepository repository, short notificationType, int entityId, string title, string body, string payload, IEnumerable<int> userIds)
		{
			createNotification(repository, NotificationTransportType.Email, notificationType, entityId, title, body, payload, userIds);
		}

		public void CreateEmail(IRepository repository, EmailMessage message)
		{
			ArgumentNullException.ThrowIfNull(message);

			mEmails.Add(requireEmailWriter(nameof(CreateEmail)).Write(repository, message));
		}

		public async Task CreateEmailFromTemplateAsync<TModel>(IRepository repository, EmailEnvelope email, TModel model, string? language = null, CancellationToken cancellationToken = default) where TModel : class, ITemplateModel
		{
			ArgumentNullException.ThrowIfNull(email);
			ArgumentNullException.ThrowIfNull(model);

			// Both are checked before the template is read, so a host that is not set up is told so rather than failing on a query.
			var writer = requireEmailWriter(nameof(CreateEmailFromTemplateAsync));
			var processor = mTemplateProcessor ?? throw new InvalidOperationException($"{nameof(CreateEmailFromTemplateAsync)} needs the template processor: call AddNotificationTemplates with the assembly that declares {typeof(TModel).Name}.");

			var rendered = await processor.RenderAsync(repository, model, language, cancellationToken);

			// The same checks as a hand-written email: a value that put a line break in the subject is refused here, not sent.
			mEmails.Add(writer.Write(repository, new EmailMessage
			{
				To = email.To,
				ToName = email.ToName,
				Subject = rendered.Subject,
				Text = rendered.Text,
				Html = rendered.Html,
				ReplyTo = email.ReplyTo,
				ReplyToName = email.ReplyToName,
				Priority = email.Priority,
				Account = email.Account,
				Ref = email.Ref,
			},
			// So the sender can keep which template, version and language this email came from next to the message.
			new TemplateReference { Key = rendered.TemplateKey, Version = rendered.Version, Language = rendered.Language }));
		}

		public async Task CreateMobileNotificationFromTemplateAsync<TModel>(IRepository repository, short notificationType, int entityId, TModel model, string payload, IEnumerable<TemplateRecipient> recipients, CancellationToken cancellationToken = default) where TModel : class, ITemplateModel
		{
			ArgumentNullException.ThrowIfNull(model);
			ArgumentNullException.ThrowIfNull(recipients);
			var processor = mTemplateProcessor ?? throw new InvalidOperationException($"{nameof(CreateMobileNotificationFromTemplateAsync)} needs the template processor: call AddNotificationTemplates with the assembly that declares {typeof(TModel).Name}.");

			// One entry per user: a user listed twice would be two NotificationToUser rows with the same key.
			var users = recipients.GroupBy(r => r.UserId).Select(g => g.First()).ToList();
			if (users.Count == 0) throw new ArgumentException("There is no one to notify.", nameof(recipients));

			// Everything is rendered before anything is written. A language is rendered once, however many users share it, and users
			// whose languages fell back to the same template (two languages with no row, both on the default) share one notification.
			var rendered = new Dictionary<string, RenderedTemplate>(StringComparer.Ordinal);
			var byTemplateLanguage = new Dictionary<string, (RenderedTemplate Template, List<int> UserIds)>(StringComparer.Ordinal);
			foreach (var user in users)
			{
				var wanted = user.Language?.Trim().ToLowerInvariant() ?? "";
				if (!rendered.TryGetValue(wanted, out var template))
				{
					template = await processor.RenderAsync(repository, model, user.Language, cancellationToken);
					rendered[wanted] = template;
				}

				if (!byTemplateLanguage.TryGetValue(template.Language, out var group)) byTemplateLanguage[template.Language] = group = (template, []);
				group.UserIds.Add(user.UserId);
			}

			foreach (var (template, _) in byTemplateLanguage.Values)
			{
				// The notification row is kept as written, and the push is built from that row.
				if (SecureMarkup.HasTags(template.Subject)) throw secureTagsInPush(template, "Subject");
				if (SecureMarkup.HasTags(template.Text)) throw secureTagsInPush(template, "Text");
			}

			foreach (var (template, userIds) in byTemplateLanguage.Values)
			{
				createNotification(repository, NotificationTransportType.FCM, notificationType, entityId, template.Subject, string.IsNullOrWhiteSpace(template.Text) ? null : template.Text, payload, userIds);
			}
		}

		private static TemplateRenderException secureTagsInPush(RenderedTemplate template, string part) =>
			new(template.TemplateKey, template.Language, template.Version, part, "secure tags are not supported in a push notification: the notification row is kept as written, so masking it would blank the push too.");

		/// <summary>Checked before the message, so a host that has email off is told that rather than which field is wrong.</summary>
		private EmailOutboxWriter requireEmailWriter(string method) =>
			mEmailWriter ?? throw new InvalidOperationException(
				$"{method} needs the email transport, but {NotificationServiceOptions.SECTION}:{nameof(NotificationServiceOptions.EmailTransport)} is not '{EmailTransportModes.Sender}'. " +
				"Set it (and NotificationService:EmailSender) or do not send the email.");

		private void createNotification(IRepository repository, byte transportType, short notificationType, int entityId, string? title, string? body, string payload, IEnumerable<int> userIds)
		{
			// A user listed twice would be two NotificationToUser rows with the same key.
			using var enumerator = userIds.Distinct().GetEnumerator();

			if (!enumerator.MoveNext())
			{
				throw new ArgumentException(nameof(userIds));
			}

			var notification = new Notification
			{
				Date = DateTime.UtcNow,
				TypeId = notificationType,
				EntityId = entityId,
				Title = title,
				Body = body,
				Payload = payload,
				TransportType = transportType,
			};

			repository.Create(notification);
			mNotifications.Add(notification);

			do
			{
				var notificationToUser = new NotificationToUser
				{
					Notification = notification,
					UserId = enumerator.Current,
				};

				repository.Create(notificationToUser);
				mNotificationsToUsers.Add(notificationToUser);
			}
			while (enumerator.MoveNext());
		}

		void IDisposable.Dispose()
		{
			if (mNotifications.Count > 0)
			{
				mLogger.LogInformation("Signaling notification queue changed");
				foreach (var notification in mNotifications.Where(n => n.Id != 0))
				{
					if (!mNotificationService.EnqueueNotification(notification.Id, mNotificationsToUsers.Where(nu => nu.NotificationId == notification.Id).Select(nu => nu.UserId).ToList(), notification.TransportType)) break;
				}
				mNotifications.Clear();
				mNotificationsToUsers.Clear();
			}

			if (mEmails.Count > 0)
			{
				// Only what the caller saved has an id. A scope that unwinds from a failed save has nothing for the relay to find.
				if (mEmails.Any(e => e.Id != 0))
				{
					mOutboxSignals?.For(Channels.Email).Notify();
				}
				mEmails.Clear();
			}
		}

		private readonly List<Notification> mNotifications = new List<Notification>();
		private readonly List<NotificationToUser> mNotificationsToUsers = new List<NotificationToUser>();
		private readonly List<OutboxMessage> mEmails = new List<OutboxMessage>();

		private readonly INotificationService mNotificationService;
		private readonly ILogger<NotificationSender> mLogger;
		private readonly OutboxSignals? mOutboxSignals;
		private readonly EmailOutboxWriter? mEmailWriter;
		private readonly TemplateProcessor? mTemplateProcessor;
	}
}
