using exs.Database.Commons.Interfaces;
using exs.notifications_contracts;
using exs.notifications_model.Notifications;
using exs.notifications_model.Outbox;
using exs.notifications_service.Interfaces;
using exs.notifications_service.Sender;
using Microsoft.Extensions.Logging;

namespace exs.notifications_service.Impl
{
	public class NotificationSender : INotificationSender, IDisposable
	{
		/// <param name="outboxSignals">Present only when something goes through a standalone sender. Poked on Dispose after a free-form email, so the email relay submits it at once.</param>
		/// <param name="emailWriter">Present only when NotificationService:EmailTransport is "Sender". Without it CreateEmail has nowhere to send to and refuses.</param>
		public NotificationSender(INotificationService notificationService, ILogger<NotificationSender> logger, OutboxSignals? outboxSignals = null, EmailOutboxWriter? emailWriter = null)
		{
			mNotificationService = notificationService;
			mLogger = logger;
			mOutboxSignals = outboxSignals;
			mEmailWriter = emailWriter;
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

			// Checked before the message, so a host that has email off is told that rather than which field is wrong.
			if (mEmailWriter == null)
			{
				throw new InvalidOperationException(
					$"CreateEmail needs the email transport, but {NotificationServiceOptions.SECTION}:{nameof(NotificationServiceOptions.EmailTransport)} is not '{EmailTransportModes.Sender}'. " +
					"Set it (and NotificationService:EmailSender) or do not send the email.");
			}

			mEmails.Add(mEmailWriter.Write(repository, message));
		}

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
	}
}
