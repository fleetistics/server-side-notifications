using exs.notifications_model.Notifications;

namespace exs.notifications_service.Interfaces
{
	public interface INotificationService
	{
		bool EnqueueNotification(Notification notification, List<int> userIds);
	}
}
