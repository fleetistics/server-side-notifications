using exs.notifications_service.Impl;
using exs.notifications_service.Interfaces;
using exs.notifications_service.Sender;
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

			// Read here, at registration, because the two modes register different services. A typo must not
			// quietly fall back to the old pipeline on a host that meant to switch.
			var mode = configuration?.GetSection(NotificationServiceOptions.SECTION)[nameof(NotificationServiceOptions.FcmTransport)];
			var useSender = string.Equals(mode, FcmTransportModes.Sender, StringComparison.OrdinalIgnoreCase);
			if (!string.IsNullOrWhiteSpace(mode) && !useSender && !string.Equals(mode, FcmTransportModes.Queue, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException(
					$"{NotificationServiceOptions.SECTION}:{nameof(NotificationServiceOptions.FcmTransport)} is '{mode}'; it must be '{FcmTransportModes.Queue}' or '{FcmTransportModes.Sender}'.");
			}

			var emailMode = configuration?.GetSection(NotificationServiceOptions.SECTION)[nameof(NotificationServiceOptions.EmailTransport)];
			var useEmail = string.Equals(emailMode, EmailTransportModes.Sender, StringComparison.OrdinalIgnoreCase);
			if (!string.IsNullOrWhiteSpace(emailMode) && !useEmail && !string.Equals(emailMode, EmailTransportModes.Off, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException(
					$"{NotificationServiceOptions.SECTION}:{nameof(NotificationServiceOptions.EmailTransport)} is '{emailMode}'; it must be '{EmailTransportModes.Off}' or '{EmailTransportModes.Sender}'.");
			}

			if (useSender)
			{
				FcmModeRegistration.AddStartupCheck(services);
			}
			if (useEmail)
			{
				EmailModeRegistration.AddStartupCheck(services);
			}

			services
				// Registered as one singleton exposed two ways: NotificationService owns the in-memory
				// Channel<T>, so INotificationService callers (NotificationSender) and the hosted
				// background loop must resolve the same instance rather than each getting their own.
				.AddSingleton<NotificationService>()
				.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>())
				.AddHostedService(sp => sp.GetRequiredService<NotificationService>())
				.AddScoped<INotificationSender, NotificationSender>()
				;

			if (useSender)
			{
				FcmModeRegistration.Add(services, configuration!);
			}
			else
			{
				services.AddSingleton<ITransportNotificationProcessor, FcmNotificationsProcessor>();
			}

			if (useEmail)
			{
				EmailModeRegistration.Add(services, configuration!);
			}
		}
	}
}
