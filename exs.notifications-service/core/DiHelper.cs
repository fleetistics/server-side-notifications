using exs.notifications_service.Impl;
using exs.notifications_service.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace exs.notifications_service.core
{
	public static class DiHelper
	{
		/// <param name="configuration">
		/// Optional. When supplied, NotificationServiceOptions is bound from its "NotificationService"
		/// section; when not, the defaults on that class apply. Optional because the host that runs this
		/// (the api-server) should bind it, but tests and any caller that is happy with the defaults
		/// should not have to construct an IConfiguration to get a working registration.
		/// </param>
		public static void RegisterServices(IServiceCollection services, IConfiguration? configuration = null)
		{
			var options = services.AddOptions<NotificationServiceOptions>();
			if (configuration != null)
			{
				options.Bind(configuration.GetSection(NotificationServiceOptions.SECTION));
			}

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
