using exs.Database.Commons.Impl;
using exs.Database.Commons.Interfaces;
using exs.fcm_sender;
using exs.fcm_sender.Fcm;
using exs.modelCommons.AppStructure;
using exs.modelCommons.UserManagement;
using exs.notifications_database;
using exs.notifications_model.Notifications;
using exs.notifications_model.Transports;
using exs.notifications_service.Impl;
using exs.notifications_service.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.notifications_pipeline.E2ETests
{
	/// <summary>
	/// Drives the whole pipeline for real: NotificationSender.CreateNotification -> NotificationService
	/// (background fan-out) -> FcmNotificationsProcessor (creates FcmQueue rows) -> Worker.pollOnceAsync
	/// (drains the queue) -> NotificationQueueProcessor (resolves sessions, sends, records FcmSent).
	/// Everything is the real production DI wiring for exs.notifications-service
	/// (exs.notifications_service.core.DiHelper, unmodified) and hand-wired equivalents for
	/// exs.fcm-sender's DiHelper - only the two things that need real infrastructure (the Postgres
	/// connection string and Firebase credentials) are substituted, with SQLite and a stub sender.
	/// </summary>
	public sealed class NotificationPipelineE2ETests : IAsyncLifetime
	{
		private readonly SqliteConnection mConnection = new("Filename=:memory:");
		private ServiceProvider mProvider = null!;
		private NotificationService mNotificationService = null!;

		public StubFcmMessageSender FcmStub { get; } = new();

		public async ValueTask InitializeAsync()
		{
			await mConnection.OpenAsync();
			mConnection.CreateFunction("now", () => DateTime.UtcNow);
			await using (var context = createContext())
			{
				await context.Database.EnsureCreatedAsync();
			}

			var services = new ServiceCollection();
			services.AddScoped(_ => createContext());
			services.AddScoped<IRepository>(sp => new EntityFrameworkRepository<NotificationDatabaseContext>(sp.GetRequiredService<NotificationDatabaseContext>()));
			services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

			// fcm-sender side, hand-wired to mirror exs.fcm_sender.core.DiHelper.RegisterServices,
			// minus the FirebaseApp/Postgres registrations it can't use in a test.
			services.AddSingleton<IFcmMessageSender>(FcmStub);
			var fcmOptions = new FcmSenderOptions { BatchSize = 50, MaxConcurrency = 1, MaxAttemptsPerSession = 1, RetryBaseDelayMilliseconds = 1 };
			services.AddSingleton(Options.Create(fcmOptions));
			services.AddSingleton<ResilientFcmSender>();
			services.AddScoped<NotificationQueueProcessor>();

			// notifications-service side: the real production DiHelper, unmodified.
			exs.notifications_service.core.DiHelper.RegisterServices(services);

			mProvider = services.BuildServiceProvider();
			mNotificationService = mProvider.GetRequiredService<NotificationService>();
		}

		public async ValueTask DisposeAsync()
		{
			await mProvider.DisposeAsync();
			await mConnection.DisposeAsync();
		}

		[Fact]
		public async Task Notification_FlowsFromSenderThroughToADeliveredFcmSentRow()
		{
			await seedActiveSessionAsync(userId: 7, "device-token");
			await mNotificationService.StartAsync(CancellationToken.None);
			// StartAsync only starts the background loop running - give its startup catch-up pass
			// (against an empty DB here) a chance to finish before seeding the live notification below.
			await Task.Delay(100);

			using (var scope = mProvider.CreateScope())
			{
				var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
				var sender = scope.ServiceProvider.GetRequiredService<INotificationSender>();
				sender.CreateNotification(repository, notificationType: 1, entityId: 123, "Title", "Body", """{"count":1}""", [7]);
				await repository.SaveAsync();
			} // scope disposal here triggers NotificationSender.Dispose() -> EnqueueNotification

			try
			{
				await waitUntilAsync(async () =>
				{
					await using var db = createContext();
					return await db.Set<FcmQueue>().AnyAsync();
				});
			}
			finally
			{
				await mNotificationService.StopAsync(CancellationToken.None);
			}

			// The fan-out stage handed off to the FCM queue - now drain it exactly like the real
			// fcm-notification-sender worker would, one poll pass at a time.
			await createWorker().pollOnceAsync(CancellationToken.None);

			FcmStub.Calls.ShouldHaveSingleItem();
			FcmStub.Calls[0].Token.ShouldBe("device-token");
			await using var finalDb = createContext();
			var sent = await finalDb.Set<FcmSent>().SingleAsync();
			sent.Error.ShouldBeNull();
			sent.UserId.ShouldBe(7);
			(await finalDb.Set<FcmQueue>().AnyAsync()).ShouldBeFalse();
			// Worth asserting here specifically: the channel now carries ids rather than entities, so
			// nothing on this path has the Notification in hand and the status is flipped by a bulk
			// ExecuteUpdate outside the change tracker. This is the only test that runs the real
			// exs.notifications_service.core.DiHelper wiring, so it is the only one that would catch
			// that update silently not reaching the row it was meant to.
			(await finalDb.Set<Notification>().SingleAsync()).Status.ShouldBe(NotificationStatus.Processed);
		}

		[Fact]
		public async Task Notification_ForUserWithNoActiveSession_EndsUpAsNoActiveSessionsWithNoFcmCall()
		{
			await mNotificationService.StartAsync(CancellationToken.None);
			await Task.Delay(100);

			using (var scope = mProvider.CreateScope())
			{
				var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
				var sender = scope.ServiceProvider.GetRequiredService<INotificationSender>();
				sender.CreateNotification(repository, notificationType: 1, entityId: 0, "Title", "Body", "", [99]);
				await repository.SaveAsync();
			}

			try
			{
				await waitUntilAsync(async () =>
				{
					await using var db = createContext();
					return await db.Set<FcmQueue>().AnyAsync();
				});
			}
			finally
			{
				await mNotificationService.StopAsync(CancellationToken.None);
			}

			await createWorker().pollOnceAsync(CancellationToken.None);

			FcmStub.Calls.ShouldBeEmpty();
			await using var finalDb = createContext();
			var sent = await finalDb.Set<FcmSent>().SingleAsync();
			sent.Error.ShouldBe("No active sessions");
			(await finalDb.Set<FcmQueue>().AnyAsync()).ShouldBeFalse();
		}

		private Worker createWorker() =>
			new(mProvider.GetRequiredService<IServiceScopeFactory>(), mProvider.GetRequiredService<IOptions<FcmSenderOptions>>(), NullLogger<Worker>.Instance);

		private NotificationDatabaseContext createContext() =>
			new(new DbContextOptionsBuilder<NotificationDatabaseContext>().UseSqlite(mConnection).Options);

		private async Task seedActiveSessionAsync(int userId, string token)
		{
			await using var context = createContext();
			context.Add(new UserSession
			{
				UserId = userId,
				StatusId = UserSessionStatus.Active,
				ClientInfo = new SessionClientInfo { PlatformId = ClientDevicePlatform.Ios, FCM_FID = token },
			});
			await context.SaveChangesAsync();
		}

		private static async Task waitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 2000)
		{
			var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
			while (!await condition())
			{
				if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
				await Task.Delay(20);
			}
		}
	}
}
