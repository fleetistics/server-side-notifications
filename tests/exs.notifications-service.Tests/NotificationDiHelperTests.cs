using exs.notifications_model.Notifications;
using exs.notifications_service.Impl;
using exs.notifications_service.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.notifications_service.Tests
{
	/// <summary>
	/// Pins what a host gets from AddNotificationSender(). It used to register its own, shorter list
	/// that omitted ITransportNotificationProcessor, so a host using it never fanned anything out -
	/// NotificationService just logged "No transport processor found for FCM." and returned.
	/// </summary>
	public class NotificationDiHelperTests
	{
		[Fact]
		public void AddNotificationSender_RegistersTheFcmTransportProcessor()
		{
			var services = new ServiceCollection();

			services.AddNotificationSender();

			var processor = services.Single(d => d.ServiceType == typeof(ITransportNotificationProcessor));
			processor.ImplementationType.ShouldBe(typeof(FcmNotificationsProcessor));
			processor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
		}

		[Fact]
		public void AddNotificationSender_RegistersSenderServiceAndHostedLoop()
		{
			var services = new ServiceCollection();

			services.AddNotificationSender();

			services.Single(d => d.ServiceType == typeof(INotificationSender)).Lifetime.ShouldBe(ServiceLifetime.Scoped);
			services.Single(d => d.ServiceType == typeof(INotificationService)).Lifetime.ShouldBe(ServiceLifetime.Singleton);
			services.ShouldContain(d => d.ServiceType == typeof(IHostedService));
		}

		[Fact]
		public void AddNotificationSender_WithoutConfiguration_UsesOptionDefaults()
		{
			var services = new ServiceCollection();
			services.AddNotificationSender();

			using var provider = services.BuildServiceProvider();
			var options = provider.GetRequiredService<IOptions<NotificationServiceOptions>>().Value;

			options.SweepIntervalSeconds.ShouldBe(new NotificationServiceOptions().SweepIntervalSeconds);
		}

		[Fact]
		public void AddNotificationSender_WithConfiguration_BindsTheNotificationServiceSection()
		{
			var configuration = new ConfigurationBuilder()
				.AddInMemoryCollection(new Dictionary<string, string?>
				{
					[$"{NotificationServiceOptions.SECTION}:SweepIntervalSeconds"] = "17",
				})
				.Build();
			var services = new ServiceCollection();
			services.AddNotificationSender(configuration);

			using var provider = services.BuildServiceProvider();
			var options = provider.GetRequiredService<IOptions<NotificationServiceOptions>>().Value;

			options.SweepIntervalSeconds.ShouldBe(17);
		}

		[Fact]
		public void AddNotificationSender_ProcessorReportsTheFcmTransport()
		{
			var services = new ServiceCollection();
			services.AddNotificationSender();

			using var provider = services.BuildServiceProvider();
			var processor = provider.GetRequiredService<ITransportNotificationProcessor>();

			// What NotificationService.processNotificationsQueue looks the processor up by.
			processor.TransportType.ShouldBe(NotificationTransportType.FCM);
		}
	}
}
