using exs.Database.Commons.Impl;
using exs.Database.Commons.Interfaces;
using exs.notifications_database;
using exs.notifications_model.Notifications;
using exs.notifications_service.Impl;
using exs.notifications_service.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace exs.notifications_service.Tests
{
	/// <summary>
	/// Drives the real NotificationService BackgroundService lifecycle (StartAsync/StopAsync) against
	/// a SQLite-backed NotificationDatabaseContext, rather than calling its private methods directly -
	/// the startup catch-up pass and the live Channel-draining loop are only reachable that way.
	/// </summary>
	public sealed class NotificationServiceTests : IAsyncLifetime
	{
		private readonly SqliteConnection mConnection = new("Filename=:memory:");

		public async ValueTask InitializeAsync()
		{
			await mConnection.OpenAsync();
			mConnection.CreateFunction("now", () => DateTime.UtcNow);
			await using var context = createContext();
			await context.Database.EnsureCreatedAsync();
		}

		public async ValueTask DisposeAsync() => await mConnection.DisposeAsync();

		[Fact]
		public async Task StartAsync_PreExistingNewNotification_IsProcessedOnStartup()
		{
			await seedNotificationAsync("Existing", [1, 2], ageMinutes: 1);
			var transportProcessor = new RecordingTransportProcessor();
			var service = createService(transportProcessor);

			await service.StartAsync(CancellationToken.None);
			try
			{
				await waitUntilAsync(() => transportProcessor.Calls.Count >= 1);
			}
			finally
			{
				await service.StopAsync(CancellationToken.None);
			}

			transportProcessor.Calls.Single().UserIds.ShouldBe([1, 2], ignoreOrder: true);
			await using var db = createContext();
			(await db.Set<Notification>().SingleAsync()).Status.ShouldBe(NotificationStatus.Processed);
		}

		[Fact]
		public async Task StartAsync_PreExistingExpiredNotification_IsMarkedExpiredAndNeverProcessed()
		{
			await seedNotificationAsync("Old", [1], ageMinutes: 90); // past the 30-minute expiration window
			var transportProcessor = new RecordingTransportProcessor();
			var service = createService(transportProcessor);

			await service.StartAsync(CancellationToken.None);
			try
			{
				await waitUntilAsync(async () =>
				{
					await using var db = createContext();
					return (await db.Set<Notification>().SingleAsync()).Status != NotificationStatus.New;
				});
			}
			finally
			{
				await service.StopAsync(CancellationToken.None);
			}

			transportProcessor.Calls.ShouldBeEmpty();
			await using var final = createContext();
			(await final.Set<Notification>().SingleAsync()).Status.ShouldBe(NotificationStatus.Expired);
		}

		[Fact]
		public async Task NoFcmTransportProcessorRegistered_LogsAndLeavesNotificationUnprocessed()
		{
			await seedNotificationAsync("Orphan", [1], ageMinutes: 1);
			var service = createService(); // no processors registered at all

			await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));
			try
			{
				await Task.Delay(200); // give the startup catch-up pass a chance to run and bail out
			}
			finally
			{
				await service.StopAsync(CancellationToken.None);
			}

			await using var db = createContext();
			(await db.Set<Notification>().SingleAsync()).Status.ShouldBe(NotificationStatus.New);
		}

		[Fact]
		public async Task EnqueueNotification_WhileRunning_IsProcessedAndPersistedAsProcessed()
		{
			var transportProcessor = new RecordingTransportProcessor();
			var service = createService(transportProcessor);
			await service.StartAsync(CancellationToken.None);
			// StartAsync only starts ExecuteAsync running in the background - give its startup
			// catch-up pass (processNotificationsTable, against an empty DB here) a chance to finish
			// before seeding, so it can't race the live notification seeded below and process it twice.
			await Task.Delay(100);

			Notification notification;
			await using (var seed = createContext())
			{
				notification = new Notification { Date = DateTime.UtcNow, TypeId = 1, Title = "Live", Body = "b", Payload = "" };
				seed.Add(notification);
				await seed.SaveChangesAsync();
			}
			service.EnqueueNotification(notification, [5]).ShouldBeTrue();

			try
			{
				await waitUntilAsync(() => transportProcessor.Calls.Count >= 1);
			}
			finally
			{
				await service.StopAsync(CancellationToken.None);
			}

			transportProcessor.Calls.Single().UserIds.ShouldBe([5]);
			await using var db = createContext();
			(await db.Set<Notification>().SingleAsync(n => n.Id == notification.Id)).Status.ShouldBe(NotificationStatus.Processed);
		}

		[Fact]
		public async Task StopAsync_CalledTwice_DoesNotThrow()
		{
			// Regression guard: Writer.Complete() throws ChannelClosedException on a second call - a
			// real hosting-stack behavior (IHost.StopAsync() can run twice, e.g. WebApplicationFactory
			// teardown) - so TryComplete() must be used instead.
			var service = createService();
			await service.StartAsync(CancellationToken.None);

			await service.StopAsync(CancellationToken.None);

			await Should.NotThrowAsync(() => service.StopAsync(CancellationToken.None));
		}

		private NotificationService createService(params ITransportNotificationProcessor[] processors) =>
			new(createScopeFactory(), processors, NullLogger<NotificationService>.Instance);

		private IServiceScopeFactory createScopeFactory()
		{
			var services = new ServiceCollection();
			// See WorkerTests for why this must match createContext() exactly rather than going
			// through AddDbContext: NotificationsModelBuilder/CommonModelBuilder memoize "model already
			// built" behind a process-wide static bool, keyed to nothing - a differently-constructed
			// DbContextOptions is a different EF model cache key and would find that guard already
			// tripped, producing a context with an empty model.
			services.AddScoped(_ => createContext());
			services.AddScoped<IRepository>(sp => new EntityFrameworkRepository<NotificationDatabaseContext>(sp.GetRequiredService<NotificationDatabaseContext>()));
			return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
		}

		private NotificationDatabaseContext createContext() =>
			new(new DbContextOptionsBuilder<NotificationDatabaseContext>().UseSqlite(mConnection).Options);

		private async Task seedNotificationAsync(string title, int[] userIds, int ageMinutes)
		{
			await using var context = createContext();
			var notification = new Notification
			{
				Date = DateTime.UtcNow - TimeSpan.FromMinutes(ageMinutes),
				TypeId = 1,
				Title = title,
				Body = "Body",
				Payload = "",
			};
			context.Add(notification);
			await context.SaveChangesAsync();

			foreach (var userId in userIds)
			{
				context.Add(new NotificationToUser { NotificationId = notification.Id, UserId = userId });
			}
			await context.SaveChangesAsync();
		}

		private static async Task waitUntilAsync(Func<bool> condition, int timeoutMs = 2000) =>
			await waitUntilAsync(() => Task.FromResult(condition()), timeoutMs);

		private static async Task waitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 2000)
		{
			var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
			while (!await condition())
			{
				if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
				await Task.Delay(20);
			}
		}

		private sealed class RecordingTransportProcessor : ITransportNotificationProcessor
		{
			public byte TransportType => NotificationTransportType.FCM;
			public List<(Notification Notification, List<int> UserIds)> Calls { get; } = [];

			public Task ProcessNotificationAsync(IRepository repository, Notification notification, List<int> usersIds, CancellationToken cancellationToken)
			{
				Calls.Add((notification, usersIds));
				return Task.CompletedTask;
			}
		}
	}
}
