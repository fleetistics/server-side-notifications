using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace exs.notifications_service
{
	public static class NotificationDiHelper
	{
		/// <summary>
		/// Fluent entry point for hosts. Deliberately a thin wrapper over core.DiHelper.RegisterServices so
		/// there is exactly one list of registrations: this used to carry its own, which had drifted and
		/// left out ITransportNotificationProcessor - without it NotificationService has nothing to fan
		/// out to and every notification stays Status=New until it expires.
		/// </summary>
		/// <param name="configuration">Optional; see core.DiHelper.RegisterServices.</param>
		public static IServiceCollection AddNotificationSender(this IServiceCollection services, IConfiguration? configuration = null)
		{
			core.DiHelper.RegisterServices(services, configuration);
			return services;
		}
	}
}
