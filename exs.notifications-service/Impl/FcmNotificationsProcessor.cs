using exs.Database.Commons.Interfaces;
using exs.notifications_model.Notifications;
using exs.notifications_model.Transports;
using exs.notifications_service.Interfaces;

namespace exs.notifications_service.Impl
{
	public class FcmNotificationsProcessor: ITransportNotificationProcessor
	{
		public FcmNotificationsProcessor()
		{
		}

		public byte TransportType => NotificationTransportType.FCM;

		public async Task ProcessNotificationAsync(IRepository repository, Notification notification, List<int> usersIds, CancellationToken cancellationToken)
		{
			// Implement the logic to process the notification and send it to the users
			// For example, you can create FcmQueue entries for each user and mark them as processed
			foreach (var userId in usersIds)
			{
				var fcmQueueEntry = new FcmQueue
				{
					Date = DateTime.UtcNow,
					NotificationId = notification.Id,
					UserId = userId
				};
				repository.Create(fcmQueueEntry);
			}
			// Mark the notification as processed
			notification.Status = NotificationStatus.Processed;
		}
	}
}
