using System.Diagnostics;
using exs.Database.Commons.Interfaces;
using exs.notifications_contracts;
using exs.notifications_model.Notifications;
using exs.notifications_model.Outbox;
using exs.notifications_service.Impl;
using exs.notifications_service.Interfaces;
using exs.notifications_service.Sender;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace exs.notifications_service.Tests.Sender
{
	/// <summary>
	/// The real NotificationService in sender mode: a notification created through INotificationSender (or
	/// found by the sweep) ends up as outbox rows, the notification is marked processed, and the relay is
	/// woken. Only the relay and puller are left out, so the rows can be looked at before anything submits them.
	/// </summary>
	public sealed class FcmModeFanOutTests : IAsyncDisposable
	{
		private readonly HostTestDatabase mDatabase = HostTestDatabase.Create();
		private readonly FakeFcmDestinationResolver mResolver = new();
		private readonly ServiceProvider mProvider;
		private readonly NotificationService mService;

		public FcmModeFanOutTests()
		{
			var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
			{
				["NotificationService:FcmTransport"] = "Sender",
				["NotificationService:FcmSender:BaseUrl"] = "http://sender.test",
				["NotificationService:FcmSender:ApiKey"] = "k",
				["NotificationService:SweepIntervalSeconds"] = "3600",
			}).Build();

			var services = new ServiceCollection();
			mDatabase.Register(services);
			services.AddNotificationSender(configuration);
			services.AddSingleton<IFcmDestinationResolver>(mResolver);
			services.AddSingleton<IFcmTokenInvalidator, FakeFcmTokenInvalidator>();
			services.RemoveAll<IHostedService>(); // only the fan-out is started, by hand, below
			mProvider = services.BuildServiceProvider();
			mService = mProvider.GetRequiredService<NotificationService>();
		}

		public async ValueTask DisposeAsync()
		{
			await mService.StopAsync(CancellationToken.None);
			await mProvider.DisposeAsync();
			await mDatabase.DisposeAsync();
		}

		private Task<List<OutboxMessage>> rowsAsync() => mDatabase.QueryAsync(db => db.Set<OutboxMessage>().AsNoTracking().OrderBy(o => o.Id).ToListAsync());

		private static async Task eventuallyAsync(Func<Task<bool>> condition, string what)
		{
			var deadline = DateTime.UtcNow.AddSeconds(10);
			while (DateTime.UtcNow < deadline)
			{
				if (await condition()) return;
				await Task.Delay(25);
			}
			throw new TimeoutException($"Timed out waiting for: {what}");
		}

		[Fact]
		public async Task ANotificationCreatedThroughTheSender_BecomesOutboxRows_AndIsMarkedProcessed()
		{
			mResolver.Destinations.Add(new FcmResolvedDestination(7, "tok-a", FcmPlatforms.Android, Ref: "s:1"));
			mResolver.Destinations.Add(new FcmResolvedDestination(7, "tok-b", FcmPlatforms.Ios, Ref: "s:2"));
			await mService.StartAsync(CancellationToken.None);
			await Task.Delay(100); // let the startup catch-up pass over the empty table finish

			using (var scope = mProvider.CreateScope())
			{
				var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
				scope.ServiceProvider.GetRequiredService<INotificationSender>()
					.CreateMobileNotification(repository, notificationType: 3, entityId: 55, "Title", "Body", """{ "k": "v" }""", [7, 8]);
				await repository.SaveAsync();
			} // disposing the scope is what hands the notification to the fan-out

			await eventuallyAsync(async () => (await rowsAsync()).Count == 3, "the fan-out to write its outbox rows");

			var rows = await rowsAsync();
			rows.Count(r => r.UserId == 7 && r.State == OutboxState.Pending).ShouldBe(2); // user 7's two devices
			var none = rows.Single(r => r.UserId == 8);                                  // user 8 has no device
			none.State.ShouldBe(OutboxState.Completed);
			none.ErrorKind.ShouldBe(OutboxErrorKind.NoDestination);
			await eventuallyAsync(async () => (await mDatabase.QueryAsync(db => db.Set<Notification>().SingleAsync())).Status == NotificationStatus.Processed,
				"the notification to be marked processed");
		}

		[Fact]
		public async Task TheRelayIsWokenOnceTheRowsAreCommitted_SoNothingWaitsForThePoll()
		{
			mResolver.Destinations.Add(new FcmResolvedDestination(7, "tok-a", FcmPlatforms.Android));
			var signal = mProvider.GetRequiredService<OutboxSignals>().For(Channels.Fcm);
			await mService.StartAsync(CancellationToken.None);
			await Task.Delay(100);

			using (var scope = mProvider.CreateScope())
			{
				var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
				scope.ServiceProvider.GetRequiredService<INotificationSender>().CreateMobileNotification(repository, 1, 1, "t", "b", "{}", [7]);
				await repository.SaveAsync();
			}

			var waited = Stopwatch.StartNew();
			await signal.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

			waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
			(await rowsAsync()).ShouldNotBeEmpty(); // woken after the commit, not before it
		}

		[Fact]
		public async Task ANotificationTheSweepFinds_IsFannedOutToTheOutboxToo()
		{
			// Written by something other than this process's own sender, as on the api-server's other paths.
			mResolver.Destinations.Add(new FcmResolvedDestination(7, "tok-a", FcmPlatforms.Android));
			await using (var context = mDatabase.CreateContext())
			{
				var notification = new Notification { Date = DateTime.UtcNow.AddMinutes(-5), TypeId = 1, Title = "t", Body = "b" };
				context.Set<Notification>().Add(notification);
				await context.SaveChangesAsync();
				context.Set<NotificationToUser>().Add(new NotificationToUser { NotificationId = notification.Id, UserId = 7 });
				await context.SaveChangesAsync();
			}

			await mService.StartAsync(CancellationToken.None); // the startup pass is the sweep

			await eventuallyAsync(async () => (await rowsAsync()).Count == 1, "the sweep to fan the notification out");
			(await rowsAsync()).Single().State.ShouldBe(OutboxState.Pending);
		}

		[Fact]
		public async Task AFanOutThatIsRunTwiceForTheSameNotification_DoesNotQueueItTwice()
		{
			// A crash between committing the rows and marking the notification processed makes the sweep
			// fan it out again. Without the processor's own check that would push every device twice.
			mResolver.Destinations.Add(new FcmResolvedDestination(7, "tok-a", FcmPlatforms.Android));
			await using (var context = mDatabase.CreateContext())
			{
				var notification = new Notification { Date = DateTime.UtcNow.AddMinutes(-5), TypeId = 1, Title = "t", Body = "b" };
				context.Set<Notification>().Add(notification);
				await context.SaveChangesAsync();
				context.Set<NotificationToUser>().Add(new NotificationToUser { NotificationId = notification.Id, UserId = 7 });
				await context.SaveChangesAsync();
			}
			await mService.StartAsync(CancellationToken.None);
			await eventuallyAsync(async () => (await rowsAsync()).Count == 1, "the first fan-out");

			// Put the notification back as if the status flip had never happened, then sweep again.
			await mDatabase.QueryAsync(db => db.Set<Notification>().ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, NotificationStatus.New)));
			await mService.StopAsync(CancellationToken.None);
			await mService.StartAsync(CancellationToken.None);
			await eventuallyAsync(async () => (await mDatabase.QueryAsync(db => db.Set<Notification>().SingleAsync())).Status == NotificationStatus.Processed,
				"the second sweep to finish");

			(await rowsAsync()).Count.ShouldBe(1);
		}
	}
}
