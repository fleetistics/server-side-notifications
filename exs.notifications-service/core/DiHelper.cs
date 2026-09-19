using exs.notifications_service.Impl;
using exs.notifications_service.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace exs.notifications_service.core
{
	public static class DiHelper
	{
		public static void RegisterServices(IServiceCollection services)
		{
			services
				// Registered as one singleton exposed two ways: NotificationService owns the in-memory
				// Channel<T>, so INotificationService callers (NotificationSender) and the hosted
				// background loop must resolve the same instance rather than each getting their own.
				.AddSingleton<NotificationService>()
				.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>())
				.AddHostedService(sp => sp.GetRequiredService<NotificationService>())
				.AddScoped<INotificationSender, NotificationSender>()
				.AddSingleton<ITransportNotificationProcessor, FcmNotificationsProcessor>()
				;
		}
	}
}
