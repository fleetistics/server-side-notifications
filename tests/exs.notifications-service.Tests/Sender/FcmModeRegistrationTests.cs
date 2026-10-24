using System.Net.Http.Headers;
using exs.notifications_service.Impl;
using exs.notifications_service.Interfaces;
using exs.notifications_service.Sender;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.notifications_service.Tests.Sender
{
	public class FcmModeRegistrationTests
	{
		private static IConfiguration config(params (string Key, string Value)[] values)
		{
			var map = new Dictionary<string, string?>();
			foreach (var (key, value) in values) map[key] = value; // a later entry overrides an earlier one
			return new ConfigurationBuilder().AddInMemoryCollection(map).Build();
		}

		private static IConfiguration senderMode(params (string Key, string Value)[] more)
		{
			var values = new List<(string, string)>
			{
				("NotificationService:FcmTransport", "Sender"),
				("NotificationService:FcmSender:BaseUrl", "https://senders.internal:5100/"),
				("NotificationService:FcmSender:ApiKey", "secret-key"),
			};
			values.AddRange(more);
			return config(values.ToArray());
		}

		private static ServiceCollection register(IConfiguration configuration)
		{
			var services = new ServiceCollection();
			services.AddLogging();
			services.AddNotificationSender(configuration);
			return services;
		}

		/// <summary>
		/// The hosted services as the host would construct them, in the order it would start them. Resolved
		/// rather than read from the descriptors, because NotificationService is registered through a factory
		/// and so has no ImplementationType to look at.
		/// </summary>
		private static List<Type> hostedTypes(IServiceCollection services)
		{
			using var provider = services.BuildServiceProvider();
			return provider.GetServices<IHostedService>().Select(h => h.GetType()).ToList();
		}

		public class QueueMode
		{
			[Theory]
			[InlineData(null)]
			[InlineData("Queue")]
			[InlineData("queue")]
			[InlineData("")]
			public void IsTheDefault_AndIsExactlyTheOldPipeline(string? mode)
			{
				var configuration = mode == null ? config() : config(("NotificationService:FcmTransport", mode));

				var services = register(configuration);

				services.Single(d => d.ServiceType == typeof(ITransportNotificationProcessor)).ImplementationType.ShouldBe(typeof(FcmNotificationsProcessor));
				hostedTypes(services).ShouldBe([typeof(NotificationService)]);
				services.Any(d => d.ServiceType == typeof(INotificationTransport)).ShouldBeFalse();
				services.Any(d => d.ServiceType == typeof(OutboxSignals)).ShouldBeFalse();
			}

			[Fact]
			public void NeedsNoSenderSettingsAndNoHooks()
			{
				var services = register(config());
				using var provider = services.BuildServiceProvider();

				Should.NotThrow(() => provider.GetRequiredService<NotificationService>());
				provider.GetService<IFcmDestinationResolver>().ShouldBeNull();
			}

			[Fact]
			public void WithoutAnyConfiguration_StillRegistersTheOldPipeline()
			{
				var services = new ServiceCollection();
				services.AddNotificationSender();

				services.Single(d => d.ServiceType == typeof(ITransportNotificationProcessor)).ImplementationType.ShouldBe(typeof(FcmNotificationsProcessor));
			}
		}

		public class SenderMode
		{
			[Theory]
			[InlineData("Sender")]
			[InlineData("sender")]
			[InlineData("SENDER")]
			public void SwitchesToTheOutboxPipeline(string mode)
			{
				var services = register(config(
					("NotificationService:FcmTransport", mode),
					("NotificationService:FcmSender:BaseUrl", "https://s.test"),
					("NotificationService:FcmSender:ApiKey", "k")));

				services.Single(d => d.ServiceType == typeof(ITransportNotificationProcessor)).ImplementationType.ShouldBe(typeof(FcmOutboxProcessor));
				services.Any(d => d.ServiceType == typeof(ITransportNotificationProcessor) && d.ImplementationType == typeof(FcmNotificationsProcessor)).ShouldBeFalse();
			}

			[Fact]
			public void AddsTheRelayThePullerAndTheStartupCheck_AlongsideTheFanOut()
			{
				var services = register(senderMode());

				hostedTypes(services).ShouldContain(typeof(NotificationService));
				hostedTypes(services).ShouldContain(typeof(OutboxRelay));
				hostedTypes(services).ShouldContain(typeof(ResultPuller));
				hostedTypes(services).ShouldContain(typeof(FcmModeStartupCheck));
				services.Single(d => d.ServiceType == typeof(INotificationTransport)).ImplementationType.ShouldBe(typeof(HttpNotificationTransport));
			}

			[Fact]
			public void TheStartupCheckRunsFirst_BeforeTheFanOutTheRelayAndThePuller()
			{
				// A host without its hooks has to stop before the fan-out gets to process anything.
				var order = hostedTypes(register(senderMode()));

				order[0].ShouldBe(typeof(FcmModeStartupCheck));
				order.IndexOf(typeof(FcmModeStartupCheck)).ShouldBeLessThan(order.IndexOf(typeof(NotificationService)));
				order.IndexOf(typeof(FcmModeStartupCheck)).ShouldBeLessThan(order.IndexOf(typeof(OutboxRelay)));
				order.IndexOf(typeof(FcmModeStartupCheck)).ShouldBeLessThan(order.IndexOf(typeof(ResultPuller)));
			}

			[Fact]
			public void TheHostedServicesCanAllBeConstructedBeforeTheHooksExist()
			{
				// The reason the hooks are looked up lazily: a host that forgot them should get the startup
				// check's message, not a bare dependency-injection failure while the services are built.
				var services = register(senderMode());

				Should.NotThrow(() => hostedTypes(services));
			}

			[Fact]
			public void TheSenderHttpClient_CarriesTheBaseUrlTheKeyAndTheTimeout()
			{
				var services = register(senderMode(("NotificationService:FcmSender:RequestTimeoutSeconds", "45")));
				using var provider = services.BuildServiceProvider();

				using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpNotificationTransport.ClientName);

				client.BaseAddress.ShouldBe(new Uri("https://senders.internal:5100/"));
				client.DefaultRequestHeaders.Authorization.ShouldBe(new AuthenticationHeaderValue("Bearer", "secret-key"));
				client.Timeout.ShouldBe(TimeSpan.FromSeconds(45));
			}

			[Fact]
			public void ABaseUrlWithoutATrailingSlash_StillResolvesRelativePathsUnderIt()
			{
				var services = register(senderMode());
				services.RemoveAll<IConfiguration>();
				var withPath = register(config(
					("NotificationService:FcmTransport", "Sender"),
					("NotificationService:FcmSender:BaseUrl", "https://gateway.internal/senders/fcm"),
					("NotificationService:FcmSender:ApiKey", "k")));
				using var provider = withPath.BuildServiceProvider();

				using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpNotificationTransport.ClientName);

				new Uri(client.BaseAddress!, "v1/messages").ToString().ShouldBe("https://gateway.internal/senders/fcm/v1/messages");
			}

			[Fact]
			public void TheOutboxSignalsAreOneInstance_SoTheFanOutAndTheRelayTalkToEachOther()
			{
				var services = register(senderMode());
				using var provider = services.BuildServiceProvider();

				provider.GetRequiredService<OutboxSignals>().ShouldBeSameAs(provider.GetRequiredService<OutboxSignals>());
				provider.GetRequiredService<OutboxSignals>().For("fcm").ShouldBeSameAs(provider.GetRequiredService<OutboxSignals>().For("fcm"));
			}

			[Fact]
			public void OptionsBindFromTheSenderSection()
			{
				var services = register(senderMode(
					("NotificationService:FcmSender:RelayBatchSize", "17"),
					("NotificationService:FcmSender:DefaultAccount", "mf5"),
					("NotificationService:FcmSender:AccountsByAppId:com.fleetistics.pssp", "pssp")));
				using var provider = services.BuildServiceProvider();

				var options = provider.GetRequiredService<IOptions<SenderTransportOptions>>().Value;

				options.RelayBatchSize.ShouldBe(17);
				options.DefaultAccount.ShouldBe("mf5");
				options.AccountsByAppId["com.fleetistics.pssp"].ShouldBe("pssp");
				options.ApiKey.ShouldBe("secret-key");
			}
		}

		public class Misconfiguration
		{
			[Theory]
			[InlineData("Rabbit")]
			[InlineData("Senders")]
			[InlineData("true")]
			public void AnUnknownMode_IsRefusedByName_RatherThanQuietlyFallingBackToTheOldPipeline(string mode)
			{
				var failure = Should.Throw<InvalidOperationException>(() => register(config(("NotificationService:FcmTransport", mode))));

				failure.Message.ShouldContain("FcmTransport");
				failure.Message.ShouldContain(mode);
				failure.Message.ShouldContain("Queue");
				failure.Message.ShouldContain("Sender");
			}

			[Theory]
			[InlineData("NotificationService:FcmSender:BaseUrl", "not a url")]
			[InlineData("NotificationService:FcmSender:BaseUrl", "ftp://senders.internal")]
			[InlineData("NotificationService:FcmSender:BaseUrl", "")]
			[InlineData("NotificationService:FcmSender:ApiKey", "")]
			public void ABadBaseUrlOrMissingKey_FailsWhenTheOptionsAreFirstRead(string key, string value)
			{
				var services = register(senderMode((key, value)));
				using var provider = services.BuildServiceProvider();

				var failure = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SenderTransportOptions>>().Value);

				failure.Message.ShouldContain(key.Split(':').Last());
			}

			[Theory]
			[InlineData("RequestTimeoutSeconds")]
			[InlineData("RelayBatchSize")]
			[InlineData("RelayMaxConcurrency")]
			[InlineData("ResultPageSize")]
			[InlineData("OutboxRetentionDays")]
			public void ANonPositiveTuningValue_IsRefusedByName(string setting)
			{
				var services = register(senderMode(($"NotificationService:FcmSender:{setting}", "0")));
				using var provider = services.BuildServiceProvider();

				Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SenderTransportOptions>>().Value)
					.Message.ShouldContain(setting);
			}

			[Fact]
			public async Task SenderModeWithoutTheHostsHooks_StopsStartupWithAMessageSayingWhatToCall()
			{
				var services = register(senderMode());
				using var provider = services.BuildServiceProvider();
				var check = provider.GetServices<IHostedService>().OfType<FcmModeStartupCheck>().Single();

				var failure = await Should.ThrowAsync<InvalidOperationException>(() => check.StartAsync(CancellationToken.None));

				failure.Message.ShouldContain(nameof(IFcmDestinationResolver));
				failure.Message.ShouldContain(nameof(IFcmTokenInvalidator));
				failure.Message.ShouldContain("AddUserSessionFcmHooks");
			}

			[Fact]
			public async Task OneHookMissing_IsNamedOnItsOwn()
			{
				var services = register(senderMode());
				services.AddSingleton<IFcmDestinationResolver, FakeFcmDestinationResolver>();
				using var provider = services.BuildServiceProvider();
				var check = provider.GetServices<IHostedService>().OfType<FcmModeStartupCheck>().Single();

				var failure = await Should.ThrowAsync<InvalidOperationException>(() => check.StartAsync(CancellationToken.None));

				failure.Message.ShouldContain(nameof(IFcmTokenInvalidator));
				failure.Message.ShouldNotContain(nameof(IFcmDestinationResolver));
			}
		}

		public class Hooks
		{
			[Fact]
			public async Task WithTheHostsHooksRegistered_TheStartupCheckPasses()
			{
				var services = register(senderMode());
				services.AddFcmSenderHooks<FakeFcmDestinationResolver, FakeFcmTokenInvalidator>();
				using var provider = services.BuildServiceProvider();
				var check = provider.GetServices<IHostedService>().OfType<FcmModeStartupCheck>().Single();

				await Should.NotThrowAsync(() => check.StartAsync(CancellationToken.None));
			}

			[Fact]
			public void TheUserSessionHooks_AreTheDefaultsForHostsThatUseUserSession()
			{
				var services = register(senderMode());
				services.AddUserSessionFcmHooks();
				using var provider = services.BuildServiceProvider();

				provider.GetRequiredService<IFcmDestinationResolver>().ShouldBeOfType<UserSessionFcmDestinationResolver>();
				provider.GetRequiredService<IFcmTokenInvalidator>().ShouldBeOfType<UserSessionFcmTokenInvalidator>();
			}

			[Fact]
			public void TheHooksAreSingletons_BecauseTheyAreCalledConcurrentlyAndKeepNoState()
			{
				var services = register(senderMode());
				services.AddUserSessionFcmHooks();

				services.Single(d => d.ServiceType == typeof(IFcmDestinationResolver)).Lifetime.ShouldBe(ServiceLifetime.Singleton);
				services.Single(d => d.ServiceType == typeof(IFcmTokenInvalidator)).Lifetime.ShouldBe(ServiceLifetime.Singleton);
			}

			[Fact]
			public void TheOrderOfRegistration_DoesNotMatter()
			{
				var services = new ServiceCollection();
				services.AddLogging();
				services.AddUserSessionFcmHooks();
				services.AddNotificationSender(senderMode());
				using var provider = services.BuildServiceProvider();

				provider.GetRequiredService<IFcmDestinationResolver>().ShouldBeOfType<UserSessionFcmDestinationResolver>();
				provider.GetServices<ITransportNotificationProcessor>().Single().ShouldBeOfType<FcmOutboxProcessor>();
			}
		}
	}
}
