using System.Net.Http.Headers;
using exs.notifications_contracts;
using exs.notifications_service.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace exs.notifications_service.Sender
{
	public static class EmailSenderHooksRegistration
	{
		/// <summary>
		/// Says where a user's email address is, and whether they are to be emailed about a notification. Registered
		/// as a singleton and called concurrently, so it must not hold per-request state. Needed only when
		/// NotificationService:EmailTransport is "Sender"; otherwise it is never used.
		/// </summary>
		public static IServiceCollection AddEmailDestinationResolver<TResolver>(this IServiceCollection services)
			where TResolver : class, IEmailDestinationResolver
		{
			services.AddSingleton<IEmailDestinationResolver, TResolver>();
			return services;
		}

		/// <summary>
		/// Replaces the default email content (the notification's title as the subject and its body as plain text)
		/// with the host's own: HTML, a template, the recipient's language.
		/// </summary>
		public static IServiceCollection AddEmailContentFactory<TFactory>(this IServiceCollection services)
			where TFactory : class, IEmailContentFactory
		{
			services.Replace(ServiceDescriptor.Singleton<IEmailContentFactory, TFactory>());
			return services;
		}

		/// <summary>
		/// What to do when an address is reported undeliverable (the mailbox does not exist). Optional: without it
		/// the bounce is only recorded on the outbox row.
		/// </summary>
		public static IServiceCollection AddEmailAddressInvalidator<TInvalidator>(this IServiceCollection services)
			where TInvalidator : class, IEmailAddressInvalidator
		{
			services.AddSingleton<IEmailAddressInvalidator, TInvalidator>();
			return services;
		}
	}

	/// <summary>Reaches the host's email hooks when first needed, with a message that says what to do; see FcmSenderHooks.</summary>
	internal static class EmailSenderHooks
	{
		public static T Require<T>(IServiceProvider services) where T : class =>
			services.GetService<T>() ?? throw new InvalidOperationException(missingMessage(typeof(T).Name));

		public static string missingMessage(string what) =>
			$"NotificationService:EmailTransport is '{EmailTransportModes.Sender}' but no {what} is registered. " +
			"Call AddEmailDestinationResolver<TResolver>().";
	}

	internal static class EmailModeRegistration
	{
		/// <summary>Registered before NotificationService, so a host without its resolver stops before anything is fanned out.</summary>
		public static void AddStartupCheck(IServiceCollection services) => services.AddHostedService<EmailModeStartupCheck>();

		/// <summary>Everything the email transport adds on top of the always-present fan-out.</summary>
		public static void Add(IServiceCollection services, IConfiguration configuration)
		{
			// The email sender's connection settings are the same settings as the FCM sender's, under the options
			// name "email", so a host can have both at once, each with its own address, key and tuning.
			services.AddOptions<SenderTransportOptions>(Channels.Email)
				.Bind(configuration.GetSection(SenderTransportOptions.EMAIL_SECTION))
				.ValidateOnStart();
			services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SenderTransportOptions>, SenderTransportOptionsValidator>());

			services.TryAddSingleton(TimeProvider.System);
			services.TryAddSingleton<OutboxSignals>();
			services.TryAddSingleton<IEmailContentFactory, DefaultEmailContentFactory>();

			services.AddHttpClient(HttpNotificationTransport.EmailClientName).ConfigureHttpClient((provider, client) =>
			{
				var options = provider.GetRequiredService<IOptionsMonitor<SenderTransportOptions>>().Get(Channels.Email);
				client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
				client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
				client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
			});

			services.AddSingleton<ITransportNotificationProcessor>(sp => new EmailOutboxProcessor(
				sp, Options.Create(emailOptions(sp)), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<EmailOutboxProcessor>>()));

			// Its presence is how NotificationSender knows CreateEmail has somewhere to send to.
			services.AddSingleton(sp => new EmailOutboxWriter(sp.GetRequiredService<IOptionsMonitor<SenderTransportOptions>>(), sp.GetRequiredService<TimeProvider>()));

			// AddSingleton<IHostedService>, not AddHostedService: with a factory the latter treats a second registration of
			// the same type as a duplicate and skips it, which would leave one of the two channels without a relay or puller.
			services.AddSingleton<IHostedService>(sp => new OutboxRelay(
				sp.GetRequiredService<IServiceScopeFactory>(), transport(sp), Options.Create(emailOptions(sp)),
				sp.GetRequiredService<OutboxSignals>().For(Channels.Email), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<OutboxRelay>>(), Channels.Email));
			services.AddSingleton<IHostedService>(sp => new ResultPuller(
				sp.GetRequiredService<IServiceScopeFactory>(), transport(sp), sp, Options.Create(emailOptions(sp)),
				sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ResultPuller>>(), Channels.Email));
		}

		private static SenderTransportOptions emailOptions(IServiceProvider services) =>
			services.GetRequiredService<IOptionsMonitor<SenderTransportOptions>>().Get(Channels.Email);

		private static HttpNotificationTransport transport(IServiceProvider services) =>
			new(services.GetRequiredService<IHttpClientFactory>(), services.GetRequiredService<ILogger<HttpNotificationTransport>>(), HttpNotificationTransport.EmailClientName);
	}

	/// <summary>
	/// The email transport cannot work without the host's resolver, which is only called when a notification created
	/// by CreateEmailNotification is fanned out. Without this check a host that forgot it would start cleanly and then
	/// fail on the first such notification.
	/// </summary>
	internal sealed class EmailModeStartupCheck : IHostedService
	{
		private readonly IServiceProvider mServices;

		public EmailModeStartupCheck(IServiceProvider services)
		{
			mServices = services;
		}

		public Task StartAsync(CancellationToken cancellationToken)
		{
			if (mServices.GetService<IEmailDestinationResolver>() == null)
			{
				throw new InvalidOperationException(EmailSenderHooks.missingMessage(nameof(IEmailDestinationResolver)));
			}
			return Task.CompletedTask;
		}

		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}
}
