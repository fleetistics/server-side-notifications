using exs.Database.Commons.Interfaces;
using exs.notifications_model.Notifications;
using exs.notifications_service.Interfaces;
using Microsoft.Extensions.Logging;

namespace exs.notifications_service.Impl
{
	public class NotificationSender : INotificationSender, IDisposable
	{
		public NotificationSender(INotificationService notificationService, ILogger<NotificationSender> logger)
		{
			mNotificationService = notificationService;
			mLogger = logger;
		}

		public void CreateNotification(IRepository repository, short notificationType, int entityId, string title, string body, string payload, IEnumerable<int> userIds)
		{
			if (!userIds.Any()) throw new ArgumentException(nameof(userIds));

			var notification = new Notification
			{
				Date = DateTime.UtcNow,
				TypeId = notificationType,
				EntityId = entityId,
				Title = title,
				Body = body,
				Payload = payload
			};
			repository.Create(notification);
			mNotifications.Add(notification);
			foreach (var userId in userIds)
			{
				var notificationToUser = new NotificationToUser
				{
					Notification = notification,
					UserId = userId,
				};
				repository.Create(notificationToUser);
				mNotificationsToUsers.Add(notificationToUser);
			}
		}

		void IDisposable.Dispose()
		{
			if (mNotifications.Any())
			{
				mLogger.LogInformation("Signaling notification queue changed");
				foreach (var notification in mNotifications)
				{
					if (!mNotificationService.EnqueueNotification(notification, mNotificationsToUsers.Where(nu => nu.NotificationId == notification.Id).Select(nu => nu.UserId).ToList())) break;
				}
				mNotifications.Clear();
				mNotificationsToUsers.Clear();
			}
		}

		private readonly List<Notification> mNotifications = new List<Notification>();
		private readonly List<NotificationToUser> mNotificationsToUsers = new List<NotificationToUser>();

		private readonly INotificationService mNotificationService;
		private readonly ILogger<NotificationSender> mLogger;
	}
}
