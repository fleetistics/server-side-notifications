using exs.notifications_model.Notifications;

namespace exs.notifications_service.Interfaces
{
	public interface INotificationService
	{
		/// <param name="transportType">The notification's Notification.TransportType: only the processors for that transport run for it.</param>
		bool EnqueueNotification(int notificationId, List<int> userIds, byte transportType);
	}
}
