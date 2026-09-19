using exs.Database.Commons.Interfaces;

namespace exs.notifications_service.Interfaces
{
	public interface INotificationSender
	{
		void CreateNotification(IRepository repository, short notificationType, int entityId, string title, string body, string payload, IEnumerable<int> userIds);
		
	}
}
