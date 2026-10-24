using exs.Database.Commons.Impl;
using exs.Database.Commons.Interfaces;
using exs.notifications_database;
using exs.notifications_model.Notifications;
using exs.notifications_service.Impl;
using exs.notifications_service.Interfaces;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.notifications_service.Tests
{
	/// <summary>
	/// Covers NotificationService against a SQLite-backed NotificationDatabaseContext, split in two:
	/// what a sweep does (driven directly through processNotificationsTable) and that the background
	/// loop actually calls it (driven through the real StartAsync/StopAsync lifecycle).
	///
	/// That split is deliberate. Everything here shares one SqliteConnection, which cannot serve
	/// concurrent commands, so a test thread querying the database while the loop is working fails
	/// intermittently with "database is locked" - the harness racing itself, not a defect in the code
	/// under test. Tests that start the loop therefore wait on the in-memory recorder only.
	/// </summary>
	public sealed class NotificationServiceTests : IAsyncLifetime
	{
		private readonly SqliteConnection mConnection = new("Filename=:memory:");
		private NotificationDatabaseContext mProbe = null!;

		public async ValueTask InitializeAsync()
		{
			await mConnection.OpenAsync();
			mConnection.CreateFunction("now", () => DateTime.UtcNow);
			await using var context = createContext();
			await context.Database.EnsureCreatedAsync();

			// Built here, while nothing else is running, and reused by every assertion below. Each
			// NotificationDatabaseContext constructed over this shared connection re-registers the now()
			// UDF on it, which fails outright if a statement is already in flight.
			mProbe = createContext();
		}

		public async ValueTask DisposeAsync()
		{
			await mProbe.DisposeAsync();
			await mConnection.DisposeAsync();
		}

		// --- sweep behaviour -------------------------------------------------------------------
		// Driven directly, on the same reasoning as WorkerTests driving pollOnceAsync: one pass, no
		// background timing to wait on, no polling against a connection someone else is using.

		[Fact]
		public async Task Sweep_ProcessesAPreExistingNewNotification()
		{
			await seedNotificationAsync("Existing", [1, 2], ageMinutes: 1);
			var transportProcessor = new RecordingTransportProcessor();

			await createService(transportProcessor).processNotificationsTable(CancellationToken.None);

			var seeded = await mProbe.Set<Notification>().AsNoTracking().SingleAsync();
			transportProcessor.Calls.Single().NotificationId.ShouldBe(seeded.Id);
			transportProcessor.Calls.Single().UserIds.ShouldBe([1, 2], ignoreOrder: true);
			seeded.Status.ShouldBe(NotificationStatus.Processed);
		}

		[Fact]
		public async Task Sweep_ExpiredNotification_IsMarkedExpiredAndNeverProcessed()
		{
			await seedNotificationAsync("Old", [1], ageMinutes: 90); // past the 30-minute expiration window
			var transportProcessor = new RecordingTransportProcessor();

			await createService(transportProcessor).processNotificationsTable(CancellationToken.None);

			transportProcessor.Calls.ShouldBeEmpty();
			(await mProbe.Set<Notification>().AsNoTracking().SingleAsync()).Status.ShouldBe(NotificationStatus.Expired);
		}

		[Fact]
		public async Task Sweep_ExpiryWindowComesFromConfiguration()
		{
			// The window used to be a hardcoded 30 minutes here and another hardcoded 30 minutes in the
			// sender's Worker, in a different assembly and a different process. This notification is an
			// hour old, so it would be expired unsent under the default and must not be under a wider one.
			await seedNotificationAsync("Old", [1], ageMinutes: 60);
			var transportProcessor = new RecordingTransportProcessor();
			var service = createService(new NotificationServiceOptions { ExpirationMinutes = 24 * 60 }, transportProcessor);

			await service.processNotificationsTable(CancellationToken.None);

			transportProcessor.Calls.Single().UserIds.ShouldBe([1]);
			(await mProbe.Set<Notification>().AsNoTracking().SingleAsync()).Status.ShouldBe(NotificationStatus.Processed);
		}

		[Fact]
		public async Task Sweep_ExpiryWindowCanBeNarrowed()
		{
			// The other direction, so the test above cannot pass just by the expiry never firing.
			await seedNotificationAsync("Recent", [1], ageMinutes: 5);
			var transportProcessor = new RecordingTransportProcessor();
			var service = createService(new NotificationServiceOptions { ExpirationMinutes = 1 }, transportProcessor);

			await service.processNotificationsTable(CancellationToken.None);

			transportProcessor.Calls.ShouldBeEmpty();
			(await mProbe.Set<Notification>().AsNoTracking().SingleAsync()).Status.ShouldBe(NotificationStatus.Expired);
		}

		[Fact]
		public async Task Sweep_NoFcmTransportProcessorRegistered_LeavesNotificationUnprocessed()
		{
			await seedNotificationAsync("Orphan", [1], ageMinutes: 1);
			var service = createService(); // no processors registered at all

			await Should.NotThrowAsync(() => service.processNotificationsTable(CancellationToken.None));

			(await mProbe.Set<Notification>().AsNoTracking().SingleAsync()).Status.ShouldBe(NotificationStatus.New);
		}

		[Fact]
		public async Task Sweep_SeveralNotifications_AllMarkedProcessedByTheOneBulkUpdate()
		{
			// Status is not written per notification as it is handled - the whole batch is flipped in a
			// single ExecuteUpdate after the loop. That update runs outside the change tracker and against
			// a list built during the loop, so it is worth pinning that every id in a multi-item batch
			// actually lands in it.
			await seedNotificationAsync("First", [1], ageMinutes: 1);
			await seedNotificationAsync("Second", [2], ageMinutes: 2);
			await seedNotificationAsync("Third", [3], ageMinutes: 3);
			var transportProcessor = new RecordingTransportProcessor();

			await createService(transportProcessor).processNotificationsTable(CancellationToken.None);

			var notifications = await mProbe.Set<Notification>().AsNoTracking().ToListAsync();
			notifications.Count.ShouldBe(3);
			notifications.ShouldAllBe(n => n.Status == NotificationStatus.Processed);
			transportProcessor.Calls.Select(c => c.NotificationId).ShouldBe(notifications.Select(n => n.Id), ignoreOrder: true);
		}

		[Fact]
		public async Task Sweep_TransportProcessorThrows_NotificationIsLeftNewSoItCanBeRetried()
		{
			// The id is only added to the bulk-update list once ProcessNotificationAsync has returned and
			// its fcm_queue rows are committed. A notification whose fan-out threw must stay New: marking
			// it Processed would lose it permanently, since nothing but the New sweep ever looks at it
			// again.
			await seedNotificationAsync("Doomed", [1], ageMinutes: 1);
			var transportProcessor = new RecordingTransportProcessor { ThrowAfterRecording = new InvalidOperationException("transport is down") };

			await createService(transportProcessor).processNotificationsTable(CancellationToken.None);

			transportProcessor.Calls.Count.ShouldBe(1);
			(await mProbe.Set<Notification>().AsNoTracking().SingleAsync()).Status.ShouldBe(NotificationStatus.New);
		}

		[Fact]
		public async Task Sweep_ShutdownMidBatch_StillRecordsWhatAlreadySucceeded()
		{
			// Guards the missing cancellation token on the bulk status update. Status is written once per
			// batch, after the loop, while fcm_queue rows are committed per notification inside it - so a
			// shutdown between the two leaves a notification at New with its queue rows already live, and
			// the next sweep fans it out again for a duplicate push to every recipient. Passing the
			// stopping token to that update is what makes this happen, which is why it does not get one.
			//
			// The first notification here is processed and committed normally; the transport then cancels
			// as the second begins, so the second fails and the batch reaches the status update with the
			// token already cancelled.
			await seedNotificationAsync("First", [1], ageMinutes: 2);
			await seedNotificationAsync("Second", [2], ageMinutes: 1);
			using var shutdown = new CancellationTokenSource();
			var transportProcessor = new RecordingTransportProcessor { CancelAtCall = shutdown, CancelAtCallNumber = 2 };

			await createService(transportProcessor).processNotificationsTable(shutdown.Token);

			transportProcessor.Calls.Count.ShouldBe(2);
			var committedId = transportProcessor.Calls[0].NotificationId;
			var notifications = await mProbe.Set<Notification>().AsNoTracking().ToListAsync();

			// The assertion that matters: this one's fcm_queue rows were committed before the shutdown, so
			// the push is happening whatever else does. If the status update honoured the cancelled token
			// this would still read New, and the next sweep would send it all over again.
			notifications.Single(n => n.Id == committedId).Status.ShouldBe(NotificationStatus.Processed);
		}

		[Fact]
		public async Task Sweep_PicksUpANotificationThatNeverWentThroughTheQueue()
		{
			// The reason the sweep exists. The in-memory queue is bounded with DropOldest, so a burst can
			// silently evict entries; before this ran periodically, an evicted notification sat at
			// Status=New until the process restarted, and if that restart came more than 30 minutes later
			// the startup pass marked it Expired instead of sending it. Seeding straight into the database
			// with no EnqueueNotification reproduces that state, and also stands in for a row written by
			// another process or a crash between commit and enqueue.
			await seedNotificationAsync("NeverEnqueued", [7], ageMinutes: 1);
			var transportProcessor = new RecordingTransportProcessor();

			await createService(transportProcessor).processNotificationsTable(CancellationToken.None);

			transportProcessor.Calls.Single().UserIds.ShouldBe([7]);
			(await mProbe.Set<Notification>().AsNoTracking().SingleAsync()).Status.ShouldBe(NotificationStatus.Processed);
		}

		[Fact]
		public async Task Sweep_LeavesNotificationsInsideTheGracePeriodAlone()
		{
			// A notification that has only just committed is probably sitting in the queue waiting to be
			// drained. Sweeping it up as well would fan it out twice and send every recipient a duplicate
			// push, so anything younger than the grace period is left alone.
			await seedNotificationAsync("JustCommitted", [1], ageMinutes: 0);
			var transportProcessor = new RecordingTransportProcessor();
			// Nothing in this test is old enough to sweep.
			var service = createService(new NotificationServiceOptions { SweepGracePeriodSeconds = 600 }, transportProcessor);

			await service.processNotificationsTable(CancellationToken.None);

			transportProcessor.Calls.ShouldBeEmpty();
			(await mProbe.Set<Notification>().AsNoTracking().SingleAsync()).Status.ShouldBe(NotificationStatus.New);
		}

		// --- transport routing -------------------------------------------------------------------
		// A notification goes out on the one transport it was created for. The sweep reads that from the row.

		[Fact]
		public async Task Sweep_GivesEachNotificationOnlyToTheProcessorsOfItsOwnTransport()
		{
			var pushId = await seedNotificationAsync("Push", [1], ageMinutes: 2, NotificationTransportType.FCM);
			var mailId = await seedNotificationAsync("Mail", [2], ageMinutes: 1, NotificationTransportType.Email);
			var push = new RecordingTransportProcessor();
			var mail = new RecordingTransportProcessor(NotificationTransportType.Email);

			await createService(push, mail).processNotificationsTable(CancellationToken.None);

			push.Calls.Single().NotificationId.ShouldBe(pushId);
			push.Calls.Single().UserIds.ShouldBe([1]);
			mail.Calls.Single().NotificationId.ShouldBe(mailId);
			mail.Calls.Single().UserIds.ShouldBe([2]);
			(await mProbe.Set<Notification>().AsNoTracking().ToListAsync()).ShouldAllBe(n => n.Status == NotificationStatus.Processed);
		}

		[Fact]
		public async Task Sweep_ARowWrittenWithoutATransportType_ReadsAsPush()
		{
			// What a row that predates the column, or any writer that never heard of it, looks like: the column is
			// left out of the INSERT altogether, so its database default is what the row gets.
			await using (var context = createContext())
			{
				await context.Database.ExecuteSqlRawAsync(
					"INSERT INTO notification (\"Date\", \"TypeId\", \"Title\", \"Body\", \"Status\") VALUES ({0}, 1, 'Legacy', 'Body', 0)", DateTime.UtcNow.AddMinutes(-1));
				var id = await context.Set<Notification>().Select(n => n.Id).SingleAsync();
				context.Add(new NotificationToUser { NotificationId = id, UserId = 4 });
				await context.SaveChangesAsync();
			}
			var push = new RecordingTransportProcessor();
			var mail = new RecordingTransportProcessor(NotificationTransportType.Email);

			await createService(push, mail).processNotificationsTable(CancellationToken.None);

			(await mProbe.Set<Notification>().AsNoTracking().SingleAsync()).TransportType.ShouldBe(NotificationTransportType.FCM);
			push.Calls.Single().UserIds.ShouldBe([4]);
			mail.Calls.ShouldBeEmpty();
		}

		[Fact]
		public async Task Sweep_ANotificationWhoseTransportHasNoProcessor_IsExpiredWithAWarning_AndTheOthersAreStillProcessed()
		{
			var mailId = await seedNotificationAsync("Mail", [1], ageMinutes: 2, NotificationTransportType.Email);
			await seedNotificationAsync("Push", [2], ageMinutes: 1, NotificationTransportType.FCM);
			var push = new RecordingTransportProcessor();
			var log = new ListLogger<NotificationService>();
			var service = new NotificationService(createScopeFactory(), [push], Options.Create(new NotificationServiceOptions()), log);

			await service.processNotificationsTable(CancellationToken.None);

			var notifications = await mProbe.Set<Notification>().AsNoTracking().ToListAsync();
			notifications.Single(n => n.Id == mailId).Status.ShouldBe(NotificationStatus.Expired);
			notifications.Single(n => n.Id != mailId).Status.ShouldBe(NotificationStatus.Processed);
			push.Calls.Count.ShouldBe(1); // the email notification was not pushed instead
			var warning = log.Entries.Single(e => e.Level == LogLevel.Warning);
			warning.Message.ShouldContain($"Notification {mailId} ");
			warning.Message.ShouldContain("Email");
		}

		[Fact]
		public async Task Sweep_AnExpiredUnroutableNotification_IsNotFoundAgainByTheNextSweep()
		{
			await seedNotificationAsync("Mail", [1], ageMinutes: 1, NotificationTransportType.Email);
			var log = new ListLogger<NotificationService>();
			var service = new NotificationService(createScopeFactory(), [new RecordingTransportProcessor()], Options.Create(new NotificationServiceOptions()), log);

			await service.processNotificationsTable(CancellationToken.None);
			await service.processNotificationsTable(CancellationToken.None);

			log.Entries.Count(e => e.Level == LogLevel.Warning).ShouldBe(1); // said once, not on every sweep until it would have expired
		}

		[Fact]
		public async Task EnqueueNotification_WhileRunning_GivesItOnlyToTheProcessorsOfTheGivenTransport()
		{
			var push = new RecordingTransportProcessor();
			var mail = new RecordingTransportProcessor(NotificationTransportType.Email);
			var service = createService(push, mail);
			await service.StartAsync(CancellationToken.None);
			await Task.Delay(150);

			var id = await seedNotificationAsync("Live", [5], ageMinutes: 0, NotificationTransportType.Email);
			service.EnqueueNotification(id, [5], NotificationTransportType.Email).ShouldBeTrue();

			try
			{
				await waitUntilAsync(() => mail.Calls.Count >= 1);
				await Task.Delay(100);
			}
			finally
			{
				await service.StopAsync(CancellationToken.None);
			}

			mail.Calls.Single().NotificationId.ShouldBe(id);
			push.Calls.ShouldBeEmpty();
		}

		// --- loop wiring -----------------------------------------------------------------------
		// These start the real BackgroundService, and so wait on the in-memory recorder rather than
		// polling the database, leaving the shared connection to the loop alone.

		[Fact]
		public async Task StartAsync_RunsTheSweepOnStartup()
		{
			await seedNotificationAsync("Existing", [1], ageMinutes: 1);
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

			transportProcessor.Calls.Single().UserIds.ShouldBe([1]);
		}

		[Fact]
		public async Task RunningLoop_RunsTheSweepAgainOnItsIntervalWithoutARestart()
		{
			// The scheduling half: the loop keeps coming back to the sweep rather than running it only
			// once at startup. That is what turns a dropped queue entry into a delay of one interval
			// rather than a loss that survives until the next deployment.
			var transportProcessor = new RecordingTransportProcessor();
			// One second is the shortest the option expresses, which is fine - the point is that a second
			// sweep happens at all, not how soon.
			var service = createService(
				new NotificationServiceOptions { SweepIntervalSeconds = 1, SweepGracePeriodSeconds = 0 },
				transportProcessor);

			await service.StartAsync(CancellationToken.None);
			// Let the startup pass finish against an empty table first, so anything picked up after this
			// can only have come from a later, scheduled sweep.
			await Task.Delay(150);
			try
			{
				await seedNotificationAsync("AfterStartup", [7], ageMinutes: 0);
				await waitUntilAsync(() => transportProcessor.Calls.Count >= 1);
			}
			finally
			{
				await service.StopAsync(CancellationToken.None);
			}

			transportProcessor.Calls.Single().UserIds.ShouldBe([7]);
		}

		[Fact]
		public async Task EnqueueNotification_WhileRunning_IsProcessedViaTheQueue()
		{
			var transportProcessor = new RecordingTransportProcessor();
			var service = createService(transportProcessor);
			await service.StartAsync(CancellationToken.None);
			// StartAsync only starts ExecuteAsync running in the background - give its startup pass
			// (against an empty table here) a chance to finish before seeding, so it cannot race the live
			// notification below and process it twice.
			await Task.Delay(150);

			Notification notification;
			await using (var seed = createContext())
			{
				notification = new Notification { Date = DateTime.UtcNow, TypeId = 1, Title = "Live", Body = "b", Payload = "" };
				seed.Add(notification);
				await seed.SaveChangesAsync();
			}
			service.EnqueueNotification(notification.Id, [5], NotificationTransportType.FCM).ShouldBeTrue();

			try
			{
				await waitUntilAsync(() => transportProcessor.Calls.Count >= 1);
			}
			finally
			{
				await service.StopAsync(CancellationToken.None);
			}

			transportProcessor.Calls.Single().NotificationId.ShouldBe(notification.Id);
			transportProcessor.Calls.Single().UserIds.ShouldBe([5]);
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
			createService(new NotificationServiceOptions(), processors);

		private NotificationService createService(NotificationServiceOptions options, params ITransportNotificationProcessor[] processors) =>
			new(createScopeFactory(), processors, Options.Create(options), NullLogger<NotificationService>.Instance);

		private IServiceScopeFactory createScopeFactory()
		{
			var services = new ServiceCollection();
			// Matches createContext() exactly rather than going through AddDbContext; see WorkerTests.
			services.AddScoped(_ => createContext());
			services.AddScoped<IRepository>(sp => new EntityFrameworkRepository<NotificationDatabaseContext>(sp.GetRequiredService<NotificationDatabaseContext>()));
			return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
		}

		private NotificationDatabaseContext createContext() =>
			new(new DbContextOptionsBuilder<NotificationDatabaseContext>().UseSqlite(mConnection).Options);

		private async Task<int> seedNotificationAsync(string title, int[] userIds, int ageMinutes, byte transportType = NotificationTransportType.FCM)
		{
			await using var context = createContext();
			var notification = new Notification
			{
				Date = DateTime.UtcNow - TimeSpan.FromMinutes(ageMinutes),
				TypeId = 1,
				Title = title,
				Body = "Body",
				Payload = "",
				TransportType = transportType,
			};
			context.Add(notification);
			await context.SaveChangesAsync();

			foreach (var userId in userIds)
			{
				context.Add(new NotificationToUser { NotificationId = notification.Id, UserId = userId });
			}
			await context.SaveChangesAsync();
			return notification.Id;
		}

		private static async Task waitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
		{
			var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
			while (!condition())
			{
				if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
				await Task.Delay(20);
			}
		}

		private sealed class RecordingTransportProcessor : ITransportNotificationProcessor
		{
			public RecordingTransportProcessor(byte transportType = NotificationTransportType.FCM) => TransportType = transportType;

			public byte TransportType { get; }
			public List<(int NotificationId, List<int> UserIds)> Calls { get; } = [];

			/// <summary>When set, every call records itself and then throws it.</summary>
			public Exception? ThrowAfterRecording { get; set; }

			/// <summary>
			/// When set, this source is cancelled once <see cref="CancelAtCallNumber"/> calls have been
			/// made - standing in for a host shutdown landing partway through a batch.
			/// </summary>
			public CancellationTokenSource? CancelAtCall { get; set; }

			public int CancelAtCallNumber { get; set; }

			public Task ProcessNotificationAsync(IRepository repository, int notificationId, List<int> usersIds, CancellationToken cancellationToken)
			{
				Calls.Add((notificationId, usersIds));
				if (CancelAtCall is not null && Calls.Count == CancelAtCallNumber)
				{
					CancelAtCall.Cancel();
				}
				return ThrowAfterRecording is null ? Task.CompletedTask : Task.FromException(ThrowAfterRecording);
			}
		}
	}
}
