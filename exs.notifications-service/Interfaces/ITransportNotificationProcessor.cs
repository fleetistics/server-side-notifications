using exs.Database.Commons.Interfaces;
using exs.notifications_model.Notifications;

namespace exs.notifications_service.Interfaces
{
	public interface ITransportNotificationProcessor
	{
		byte TransportType { get; }
		Task ProcessNotificationAsync(IRepository repository, Notification notification, List<int> usersIds, CancellationToken cancellationToken);
	}
}
