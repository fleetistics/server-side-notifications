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
	public static class FcmSenderHooksRegistration
	{
		/// <summary>
		/// Supplies the two things only the host can do when pushes go through a standalone sender: find a
		/// user's devices, and drop a dead token. Both are registered as singletons and called concurrently,
		/// so they must not hold per-request state. Needed only when NotificationService:FcmTransport is
		/// "Sender"; in the default "Queue" mode they are never used.
		/// </summary>
		public static IServiceCollection AddFcmSenderHooks<TResolver, TInvalidator>(this IServiceCollection services)
			where TResolver : class, IFcmDestinationResolver
			where TInvalidator : class, IFcmTokenInvalidator
		{
			services.AddSingleton<IFcmDestinationResolver, TResolver>();
			services.AddSingleton<IFcmTokenInvalidator, TInvalidator>();
			return services;
		}

		/// <summary>
		/// The hooks for a host that keeps its devices in the estate's user_session table: a user's destinations
		/// are the tokens of their active iOS and Android sessions, and a dead token is cleared from them.
		/// </summary>
		public static IServiceCollection AddUserSessionFcmHooks(this IServiceCollection services) =>
			services.AddFcmSenderHooks<UserSessionFcmDestinationResolver, UserSessionFcmTokenInvalidator>();
	}

	/// <summary>
	/// How the sender-mode services reach the host's hooks: when first needed, and with a message that says
	/// what to do. They are deliberately not constructor dependencies. Hosted services are all constructed
	/// before any starts, so a missing hook would fail there as a bare "unable to resolve service" naming no
	/// remedy, and before the startup check below ever got to run.
	/// </summary>
	internal static class FcmSenderHooks
	{
		public static T Require<T>(IServiceProvider services) where T : class =>
			services.GetService<T>() ?? throw new InvalidOperationException(missingMessage(typeof(T).Name));

		public static string missingMessage(string what) =>
			$"NotificationService:FcmTransport is '{FcmTransportModes.Sender}' but no {what} is registered. " +
			"Call AddUserSessionFcmHooks() (hosts that use the user_session table) or AddFcmSenderHooks<TResolver, TInvalidator>().";
	}

	internal static class FcmModeRegistration
	{
		/// <summary>
		/// Registered before NotificationService, so it is the first thing started: a host without its hooks
		/// stops there, before the fan-out has had a chance to process anything.
		/// </summary>
		public static void AddStartupCheck(IServiceCollection services) => services.AddHostedService<FcmModeStartupCheck>();

		/// <summary>Everything the "Sender" transport mode adds on top of the always-present fan-out.</summary>
		public static void Add(IServiceCollection services, IConfiguration configuration)
		{
			services.AddOptions<SenderTransportOptions>()
				.Bind(configuration.GetSection(SenderTransportOptions.FCM_SECTION))
				.ValidateOnStart();
			services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SenderTransportOptions>, SenderTransportOptionsValidator>());
			services.TryAddSingleton(TimeProvider.System);

			services.AddHttpClient(HttpNotificationTransport.ClientName).ConfigureHttpClient((provider, client) =>
			{
				var options = provider.GetRequiredService<IOptions<SenderTransportOptions>>().Value;
				client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
				client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
				client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
			});

			services.TryAddSingleton<OutboxSignals>();
			services.AddSingleton<INotificationTransport, HttpNotificationTransport>();
			services.AddSingleton<ITransportNotificationProcessor, FcmOutboxProcessor>();

			// AddSingleton<IHostedService>, not AddHostedService: with a factory the latter treats a second registration of
			// the same type as a duplicate and skips it, which would leave one of the two channels without a relay or puller.
			services.AddSingleton<IHostedService>(sp => new OutboxRelay(
				sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<INotificationTransport>(), sp.GetRequiredService<IOptions<SenderTransportOptions>>(),
				sp.GetRequiredService<OutboxSignals>().For(Channels.Fcm), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<OutboxRelay>>(), Channels.Fcm));
			services.AddSingleton<IHostedService>(sp => new ResultPuller(
				sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<INotificationTransport>(), sp, sp.GetRequiredService<IOptions<SenderTransportOptions>>(),
				sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ResultPuller>>(), Channels.Fcm));
		}
	}

	/// <summary>
	/// Sender mode cannot work without the host's hooks, and the resolver is only called when a notification
	/// is fanned out. Without this check a host that forgot to register them would start cleanly and then
	/// fail on the first notification.
	/// </summary>
	internal sealed class FcmModeStartupCheck : IHostedService
	{
		private readonly IServiceProvider mServices;

		public FcmModeStartupCheck(IServiceProvider services)
		{
			mServices = services;
		}

		public Task StartAsync(CancellationToken cancellationToken)
		{
			var missing = new List<string>();
			if (mServices.GetService<IFcmDestinationResolver>() == null) missing.Add(nameof(IFcmDestinationResolver));
			if (mServices.GetService<IFcmTokenInvalidator>() == null) missing.Add(nameof(IFcmTokenInvalidator));

			if (missing.Count > 0)
			{
				throw new InvalidOperationException(FcmSenderHooks.missingMessage(string.Join(" and no ", missing)));
			}
			return Task.CompletedTask;
		}

		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}
}
