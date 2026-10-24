using System.Net.Http.Headers;
using exs.notifications_contracts;
using exs.notifications_service.Impl;
using exs.notifications_service.Interfaces;
using exs.notifications_service.Sender;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.notifications_service.Tests.Sender
{
	public class EmailModeRegistrationTests
	{
		private static IConfiguration config(params (string Key, string Value)[] values)
		{
			var map = new Dictionary<string, string?>();
			foreach (var (key, value) in values) map[key] = value; // a later entry overrides an earlier one
			return new ConfigurationBuilder().AddInMemoryCollection(map).Build();
		}

		private static (string, string)[] email(params (string Key, string Value)[] more)
		{
			var values = new List<(string, string)>
			{
				("NotificationService:EmailTransport", "Sender"),
				("NotificationService:EmailSender:BaseUrl", "https://mail.internal:5101/"),
				("NotificationService:EmailSender:ApiKey", "mail-key"),
			};
			values.AddRange(more);
			return values.ToArray();
		}

		private static (string, string)[] push() =>
		[
			("NotificationService:FcmTransport", "Sender"),
			("NotificationService:FcmSender:BaseUrl", "https://push.internal:5100/"),
			("NotificationService:FcmSender:ApiKey", "push-key"),
		];

		private static ServiceCollection register(IConfiguration configuration)
		{
			var services = new ServiceCollection();
			services.AddLogging();
			services.AddNotificationSender(configuration);
			return services;
		}

		private static ServiceCollection withHook(ServiceCollection services)
		{
			services.AddEmailDestinationResolver<FakeEmailDestinationResolver>();
			return services;
		}

		private static List<Type> hostedTypes(IServiceCollection services)
		{
			using var provider = services.BuildServiceProvider();
			return provider.GetServices<IHostedService>().Select(h => h.GetType()).ToList();
		}

		private static List<Type> processorTypes(IServiceCollection services)
		{
			using var provider = services.BuildServiceProvider();
			return provider.GetServices<ITransportNotificationProcessor>().Select(p => p.GetType()).ToList();
		}

		public class Off
		{
			[Theory]
			[InlineData(null)]
			[InlineData("Off")]
			[InlineData("off")]
			[InlineData("")]
			public void IsTheDefault_AndAddsNothing(string? mode)
			{
				var services = register(mode == null ? config() : config(("NotificationService:EmailTransport", mode)));

				processorTypes(services).ShouldBe([typeof(FcmNotificationsProcessor)]);
				hostedTypes(services).ShouldBe([typeof(NotificationService)]);
				services.Any(d => d.ServiceType == typeof(IEmailContentFactory)).ShouldBeFalse();
			}

			[Fact]
			public void NeedsNoEmailSettingsAndNoHooks()
			{
				Should.NotThrow(() => hostedTypes(register(config())));
			}

			[Fact]
			public void ATypoInTheMode_StopsTheHostInsteadOfQuietlySendingNoEmail()
			{
				var failure = Should.Throw<InvalidOperationException>(() => register(config(("NotificationService:EmailTransport", "Sendr"))));

				failure.Message.ShouldContain("EmailTransport");
				failure.Message.ShouldContain("Sendr");
			}
		}

		public class SenderMode
		{
			[Fact]
			public void AddsTheEmailProcessor_NextToTheExistingPushOne()
			{
				var services = withHook(register(config(email())));

				processorTypes(services).ShouldBe([typeof(FcmNotificationsProcessor), typeof(EmailOutboxProcessor)], ignoreOrder: true);
			}

			[Fact]
			public void StartsTheCheckFirst_ThenTheFanOut_ThenAnEmailRelayAndPuller()
			{
				var services = withHook(register(config(email())));

				var hosted = hostedTypes(services);

				hosted.Count.ShouldBe(4);
				hosted[0].Name.ShouldBe("EmailModeStartupCheck");
				hosted[1].ShouldBe(typeof(NotificationService));
				hosted.ShouldContain(typeof(OutboxRelay));
				hosted.ShouldContain(typeof(ResultPuller));
			}

			[Fact]
			public void TogetherWithPushSenderMode_EachChannelGetsItsOwnRelayAndPuller()
			{
				var services = withHook(register(config(push().Concat(email()).ToArray())));
				services.AddSingleton<IFcmDestinationResolver, FakeFcmDestinationResolver>();
				services.AddSingleton<IFcmTokenInvalidator, FakeFcmTokenInvalidator>();

				var hosted = hostedTypes(services);

				hosted.Count(t => t == typeof(OutboxRelay)).ShouldBe(2);
				hosted.Count(t => t == typeof(ResultPuller)).ShouldBe(2);
				processorTypes(services).ShouldBe([typeof(FcmOutboxProcessor), typeof(EmailOutboxProcessor)], ignoreOrder: true);
			}

			[Fact]
			public void TheEmailSenderHasItsOwnOptions_SeparateFromThePushSenders()
			{
				var configuration = config(push().Concat(email(("NotificationService:EmailSender:DefaultAccount", "noreply"), ("NotificationService:EmailSender:RelayBatchSize", "7"))).ToArray());
				using var provider = withHook(register(configuration)).BuildServiceProvider();

				var mail = provider.GetRequiredService<IOptionsMonitor<SenderTransportOptions>>().Get("email");
				var pushOptions = provider.GetRequiredService<IOptions<SenderTransportOptions>>().Value;

				mail.BaseUrl.ShouldBe("https://mail.internal:5101/");
				mail.ApiKey.ShouldBe("mail-key");
				mail.DefaultAccount.ShouldBe("noreply");
				mail.RelayBatchSize.ShouldBe(7);
				pushOptions.BaseUrl.ShouldBe("https://push.internal:5100/");
				pushOptions.ApiKey.ShouldBe("push-key");
				pushOptions.DefaultAccount.ShouldBeNull();
				pushOptions.RelayBatchSize.ShouldBe(100);
			}

			[Fact]
			public void TheEmailHttpClient_HasTheEmailSendersAddressAndKey()
			{
				using var provider = withHook(register(config(push().Concat(email()).ToArray()))).BuildServiceProvider();
				var factory = provider.GetRequiredService<IHttpClientFactory>();

				using var mail = factory.CreateClient(HttpNotificationTransport.EmailClientName);
				using var pushClient = factory.CreateClient(HttpNotificationTransport.ClientName);

				new Uri(mail.BaseAddress!, "v1/messages").ToString().ShouldBe("https://mail.internal:5101/v1/messages");
				mail.DefaultRequestHeaders.Authorization.ShouldBe(new AuthenticationHeaderValue("Bearer", "mail-key"));
				new Uri(pushClient.BaseAddress!, "v1/messages").ToString().ShouldBe("https://push.internal:5100/v1/messages");
				pushClient.DefaultRequestHeaders.Authorization.ShouldBe(new AuthenticationHeaderValue("Bearer", "push-key"));
			}

			[Fact]
			public void ForTheDefaultContentFactory_AHostsOwnReplacesIt_WhicheverOrderTheyAreRegisteredIn()
			{
				var after = withHook(register(config(email())));
				after.AddEmailContentFactory<FakeEmailContentFactory>();
				var before = new ServiceCollection();
				before.AddLogging();
				before.AddEmailContentFactory<FakeEmailContentFactory>();
				before.AddNotificationSender(config(email()));
				before.AddEmailDestinationResolver<FakeEmailDestinationResolver>();

				foreach (var services in new[] { after, before })
				{
					using var provider = services.BuildServiceProvider();
					provider.GetRequiredService<IEmailContentFactory>().ShouldBeOfType<FakeEmailContentFactory>();
				}
			}

			[Fact]
			public void WithoutAHostsOwn_TheDefaultContentIsUsed()
			{
				using var provider = withHook(register(config(email()))).BuildServiceProvider();

				provider.GetRequiredService<IEmailContentFactory>().ShouldBeOfType<DefaultEmailContentFactory>();
			}

			[Fact]
			public async Task WithoutTheResolver_TheHostStopsAtStartup_SayingWhatToCall()
			{
				var services = register(config(email())); // no AddEmailDestinationResolver
				using var provider = services.BuildServiceProvider();
				var check = provider.GetServices<IHostedService>().First(h => h.GetType().Name == "EmailModeStartupCheck");

				var failure = await Should.ThrowAsync<InvalidOperationException>(() => check.StartAsync(CancellationToken.None));

				failure.Message.ShouldContain(nameof(IEmailDestinationResolver));
				failure.Message.ShouldContain("AddEmailDestinationResolver");
			}

			[Fact]
			public async Task WithTheResolver_TheStartupCheckPasses()
			{
				using var provider = withHook(register(config(email()))).BuildServiceProvider();
				var check = provider.GetServices<IHostedService>().First(h => h.GetType().Name == "EmailModeStartupCheck");

				await Should.NotThrowAsync(() => check.StartAsync(CancellationToken.None));
			}

			[Fact]
			public void AMissingKey_NamesTheEmailSection_NotThePushOne()
			{
				var configuration = config(email(("NotificationService:EmailSender:ApiKey", "")));
				using var provider = withHook(register(configuration)).BuildServiceProvider();

				var failure = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptionsMonitor<SenderTransportOptions>>().Get("email"));

				failure.Message.ShouldContain("NotificationService:EmailSender:ApiKey");
				failure.Message.ShouldNotContain("NotificationService:FcmSender:");
			}

			[Fact]
			public void ABadEmailSenderUrl_IsRefused()
			{
				var configuration = config(email(("NotificationService:EmailSender:BaseUrl", "not a url")));
				using var provider = withHook(register(configuration)).BuildServiceProvider();

				Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptionsMonitor<SenderTransportOptions>>().Get("email"))
					.Message.ShouldContain("EmailSender:BaseUrl");
			}

			[Fact]
			public void TheEmailSenderSettingsAreAllThatIsNeeded_NoSettingSaysWhichNotificationsToEmail()
			{
				// No setting says which notifications are mailed: the caller chooses, per notification, by calling CreateEmailNotification.
				using var provider = withHook(register(config(email()))).BuildServiceProvider();

				Should.NotThrow(() => provider.GetServices<IHostedService>().ToList());
			}

			[Fact]
			public void TheFreeFormEmailWriter_IsRegistered_OnlyWhenTheEmailTransportIsOn()
			{
				using var on = withHook(register(config(email()))).BuildServiceProvider();
				using var off = register(config()).BuildServiceProvider();

				on.GetService<EmailOutboxWriter>().ShouldNotBeNull();
				off.GetService<EmailOutboxWriter>().ShouldBeNull();
			}

			[Fact]
			public void TheEmailSettingsAreCheckedAtStartup_NotOnTheFirstMail()
			{
				var configuration = config(email(("NotificationService:EmailSender:RelayBatchSize", "0")));
				using var provider = withHook(register(configuration)).BuildServiceProvider();

				Should.Throw<OptionsValidationException>(() => provider.GetServices<IHostedService>().ToList());
			}
		}
	}
}
