using exs.notifications_service.Impl;
using exs.notifications_service.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace exs.notifications_service
{
	public static class NotificationDiHelper
	{
		public static IServiceCollection AddNotificationSender(this IServiceCollection services)
		{
			return services
				.AddScoped<INotificationSender, NotificationSender>()
				.AddSingleton<INotificationService, NotificationService>()
				.AddHostedService(sp => (NotificationService)sp.GetRequiredService<INotificationService>());
		}
	}
}
