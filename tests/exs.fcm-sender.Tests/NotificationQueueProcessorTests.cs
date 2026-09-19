using exs.Database.Commons.Impl;
using exs.Database.Commons.Interfaces;
using exs.fcm_sender.Fcm;
using exs.modelCommons.AppStructure;
using exs.modelCommons.UserManagement;
using exs.notifications_database;
using exs.notifications_model.Notifications;
using exs.notifications_model.Transports;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.fcm_sender.Tests
{
	/// <summary>
	/// Backs NotificationQueueProcessor with a real NotificationDatabaseContext (SQLite, in-memory)
	/// rather than a mocked IRepository - its behavior (owned/complex ClientInfo filtering, the
	/// IsProcessing resume path) is inseparable from real EF query translation, which a mock would
	/// either badly reimplement or not exercise at all. One open SqliteConnection per test keeps its
	/// in-memory DB alive across the multiple context instances used - mirroring the real Worker's
	/// "poller scope loads, processor scope writes" split.
	/// </summary>
	public sealed class NotificationQueueProcessorTests : IAsyncLifetime
	{
		private readonly SqliteConnection mConnection = new("Filename=:memory:");

		public async ValueTask InitializeAsync()
		{
			await mConnection.OpenAsync();
			// LatestUpdate columns are DEFAULT now() (a Postgres function, per
			// BaseDatabaseContext.OnModelCreating) - SQLite has no such builtin, so register one
			// rather than working around every table that carries a LatestUpdate column (UserSession).
			mConnection.CreateFunction("now", () => DateTime.UtcNow);
			await using var context = createContext();
			await context.Database.EnsureCreatedAsync();
		}

		public async ValueTask DisposeAsync() => await mConnection.DisposeAsync();

		[Fact]
		public async Task ProcessAsync_NotificationNavigationNotLoaded_Throws()
		{
			var processor = createProcessor(new StubFcmMessageSender(FcmSendResult.Ok("unused")));
			var queueItem = new FcmQueue
			{
				Id = 1,
				NotificationId = 1,
				UserId = 1,
				Date = DateTime.UtcNow,
				Notification = null, // as if the caller forgot .Include(q => q.Notification)
			};

			await Should.ThrowAsync<InvalidOperationException>(() => processor.ProcessAsync(queueItem, CancellationToken.None));
		}

		[Fact]
		public async Task ProcessAsync_NoActiveSessions_RecordsErrorAndRemovesQueueRow()
		{
			var queueItem = await seedNotificationAndQueueItemAsync(userId: 1);
			var stub = new StubFcmMessageSender(FcmSendResult.Ok("unused"));

			await createProcessor(stub).ProcessAsync(queueItem, CancellationToken.None);

			stub.Calls.ShouldBeEmpty(); // nobody to send to
			await using var db = createContext();
			var sent = await db.Set<FcmSent>().SingleAsync();
			sent.Error.ShouldBe("No active sessions");
			(await db.Set<FcmQueue>().AnyAsync()).ShouldBeFalse();
		}

		[Fact]
		public async Task ProcessAsync_OneActiveSessionSucceeds_RecordsOkAndRemovesQueueRow()
		{
			var queueItem = await seedNotificationAndQueueItemAsync(userId: 1);
			var sessionId = await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Ios, "token-1");
			var stub = new StubFcmMessageSender(FcmSendResult.Ok("msg-1"));

			await createProcessor(stub).ProcessAsync(queueItem, CancellationToken.None);

			stub.Calls.Count.ShouldBe(1);
			stub.Calls[0].Token.ShouldBe("token-1");
			await using var db = createContext();
			var sent = await db.Set<FcmSent>().SingleAsync();
			sent.Error.ShouldBeNull();
			sent.UserSessionId.ShouldBe(sessionId);
			sent.FcmToken.ShouldBe("token-1");
			(await db.Set<FcmQueue>().AnyAsync()).ShouldBeFalse();
		}

		[Fact]
		public async Task ProcessAsync_OneActiveSessionFailsPermanently_RecordsErrorButStillRemovesQueueRow()
		{
			// A single-session queue row is removed after its one (internally-retried) attempt
			// regardless of outcome - there's no separate "no active sessions" state to fall into.
			var queueItem = await seedNotificationAndQueueItemAsync(userId: 1);
			await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Ios, "token-1");
			var stub = new StubFcmMessageSender(FcmSendResult.Failed(FcmSendOutcome.PermanentError, "UNREGISTERED", "dead token"));

			await createProcessor(stub).ProcessAsync(queueItem, CancellationToken.None);

			await using var db = createContext();
			var sent = await db.Set<FcmSent>().SingleAsync();
			sent.Error.ShouldBe("dead token");
			(await db.Set<FcmQueue>().AnyAsync()).ShouldBeFalse();
		}

		[Fact]
		public async Task ProcessAsync_MultipleActiveSessions_SendsToEachAndRecordsOneFcmSentPerSession()
		{
			// Regression guard: FcmSent used to be keyed on (UserId, NotificationId) alone, which
			// collided the moment a user had more than one active session for the same notification.
			var queueItem = await seedNotificationAndQueueItemAsync(userId: 1);
			var session1 = await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Ios, "token-1");
			var session2 = await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Android, "token-2");
			var stub = new StubFcmMessageSender(
				FcmSendResult.Ok("msg-1"),
				FcmSendResult.Failed(FcmSendOutcome.PermanentError, "UNREGISTERED", "dead"));

			await Should.NotThrowAsync(() => createProcessor(stub).ProcessAsync(queueItem, CancellationToken.None));

			stub.Calls.Count.ShouldBe(2);
			await using var db = createContext();
			var sentRows = await db.Set<FcmSent>().ToListAsync();
			sentRows.Count.ShouldBe(2);
			sentRows.ShouldContain(s => s.UserSessionId == session1 && s.Error == null);
			sentRows.ShouldContain(s => s.UserSessionId == session2 && s.Error == "dead");
			(await db.Set<FcmQueue>().AnyAsync()).ShouldBeFalse();
		}

		[Fact]
		public async Task ProcessAsync_OnlySendsToActiveMobileSessionsWithATokenForThatUser()
		{
			var queueItem = await seedNotificationAndQueueItemAsync(userId: 1);
			await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Ios, "good-token"); // included
			await seedUserSessionAsync(userId: 1, UserSessionStatus.LoggedOutByUser, ClientDevicePlatform.Ios, "logged-out-token"); // wrong status
			await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Windows, "windows-token"); // wrong platform
			await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Android, ""); // no token
			await seedUserSessionAsync(userId: 2, UserSessionStatus.Active, ClientDevicePlatform.Ios, "other-users-token"); // wrong user
			var stub = new StubFcmMessageSender(FcmSendResult.Ok("msg"));

			await createProcessor(stub).ProcessAsync(queueItem, CancellationToken.None);

			stub.Calls.Count.ShouldBe(1);
			stub.Calls[0].Token.ShouldBe("good-token");
		}

		[Fact]
		public async Task ProcessAsync_TransientFailureThenSuccess_RetriesInMemoryAndRecordsOk()
		{
			var queueItem = await seedNotificationAndQueueItemAsync(userId: 1);
			await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Ios, "token-1");
			var stub = new StubFcmMessageSender(
				FcmSendResult.Failed(FcmSendOutcome.TransientError, "UNAVAILABLE", "temporary"),
				FcmSendResult.Ok("msg-1"));

			await createProcessor(stub, maxAttempts: 3).ProcessAsync(queueItem, CancellationToken.None);

			stub.Calls.Count.ShouldBe(2);
			await using var db = createContext();
			var sent = await db.Set<FcmSent>().SingleAsync();
			sent.Error.ShouldBeNull();
		}

		[Fact]
		public async Task ProcessAsync_ResumingAfterPriorPartialSuccess_SkipsAlreadySucceededSessionsAndFinishesTheRest()
		{
			// Simulates a crash between the two SaveAsync calls in the multi-session loop: session 1
			// already has a successful FcmSent row and the queue row was left with IsProcessing=true,
			// but session 2 was never attempted.
			var queueItem = await seedNotificationAndQueueItemAsync(userId: 1);
			var session1 = await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Ios, "token-1");
			var session2 = await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Android, "token-2");
			await using (var seed = createContext())
			{
				seed.Add(new FcmSent
				{
					NotificationId = queueItem.NotificationId,
					UserId = 1,
					UserSessionId = session1,
					FcmToken = "token-1",
					Error = null,
					Date = DateTime.UtcNow,
				});
				var trackedQueue = await seed.Set<FcmQueue>().SingleAsync(q => q.Id == queueItem.Id);
				trackedQueue.IsProcessing = true;
				await seed.SaveChangesAsync();
			}
			queueItem.IsProcessing = true;
			var stub = new StubFcmMessageSender(FcmSendResult.Ok("msg-2"));

			await createProcessor(stub).ProcessAsync(queueItem, CancellationToken.None);

			stub.Calls.Count.ShouldBe(1); // only session 2 - session 1 is already recorded as sent
			stub.Calls[0].Token.ShouldBe("token-2");
			await using var db = createContext();
			var sentRows = await db.Set<FcmSent>().ToListAsync();
			sentRows.Count.ShouldBe(2);
			sentRows.ShouldContain(s => s.UserSessionId == session2 && s.Error == null);
			(await db.Set<FcmQueue>().AnyAsync()).ShouldBeFalse();
		}

		[Fact]
		public async Task ProcessAsync_ResumingWithOneSucceededOneFailedOneNeverAttempted_SkipsOnlyTheSucceededOne()
		{
			// The "already handled" filter only excludes sessions with a prior *successful* FcmSent
			// row (Error == null) - a session that failed last time is deliberately left retryable, on
			// the theory that a duplicate push is a fine failure mode but a silently dropped one isn't.
			// This pins that down for all three states in one resume pass.
			var queueItem = await seedNotificationAndQueueItemAsync(userId: 1);
			var session1 = await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Ios, "token-1");
			var session2 = await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Android, "token-2");
			var session3 = await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Ios, "token-3");
			await using (var seed = createContext())
			{
				seed.Add(new FcmSent
				{
					NotificationId = queueItem.NotificationId,
					UserId = 1,
					UserSessionId = session1,
					FcmToken = "token-1",
					Error = null, // succeeded last time - must not be retried
					Date = DateTime.UtcNow,
				});
				seed.Add(new FcmSent
				{
					NotificationId = queueItem.NotificationId,
					UserId = 1,
					UserSessionId = session2,
					FcmToken = "token-2",
					Error = "temporary failure", // failed last time - must still be retried
					Date = DateTime.UtcNow,
				});
				var trackedQueue = await seed.Set<FcmQueue>().SingleAsync(q => q.Id == queueItem.Id);
				trackedQueue.IsProcessing = true;
				await seed.SaveChangesAsync();
			}
			queueItem.IsProcessing = true;
			var stub = new StubFcmMessageSender(FcmSendResult.Ok("msg-2-retry"), FcmSendResult.Ok("msg-3"));

			await createProcessor(stub).ProcessAsync(queueItem, CancellationToken.None);

			stub.Calls.Count.ShouldBe(2); // session 1 skipped; session 2 retried; session 3 attempted for the first time
			stub.Calls.Select(c => c.Token).ShouldBe(["token-2", "token-3"], ignoreOrder: true);
			await using var db = createContext();
			var sentRows = await db.Set<FcmSent>().ToListAsync();
			sentRows.Count(s => s.UserSessionId == session1).ShouldBe(1); // untouched original success row
			sentRows.Count(s => s.UserSessionId == session2).ShouldBe(2); // original failure + this retry
			sentRows.Count(s => s.UserSessionId == session3).ShouldBe(1); // first attempt
			sentRows.ShouldContain(s => s.UserSessionId == session2 && s.Error == null);
			sentRows.ShouldContain(s => s.UserSessionId == session3 && s.Error == null);
			(await db.Set<FcmQueue>().AnyAsync()).ShouldBeFalse();
		}

		private NotificationDatabaseContext createContext() =>
			new(new DbContextOptionsBuilder<NotificationDatabaseContext>().UseSqlite(mConnection).Options);

		private NotificationQueueProcessor createProcessor(IFcmMessageSender sender, int maxAttempts = 1)
		{
			IRepository repository = new EntityFrameworkRepository<NotificationDatabaseContext>(createContext());
			var resilientSender = new ResilientFcmSender(sender, Options.Create(new FcmSenderOptions
			{
				MaxAttemptsPerSession = maxAttempts,
				RetryBaseDelayMilliseconds = 1,
			}));
			return new NotificationQueueProcessor(repository, resilientSender, NullLogger<NotificationQueueProcessor>.Instance);
		}

		private async Task<FcmQueue> seedNotificationAndQueueItemAsync(int userId, short typeId = 1, int? entityId = null, string payload = "")
		{
			await using var context = createContext();
			var notification = new Notification
			{
				Date = DateTime.UtcNow,
				TypeId = typeId,
				EntityId = entityId,
				Title = "Title",
				Body = "Body",
				Payload = payload,
			};
			context.Add(notification);
			await context.SaveChangesAsync();

			var queue = new FcmQueue
			{
				NotificationId = notification.Id,
				UserId = userId,
				Date = DateTime.UtcNow,
			};
			context.Add(queue);
			await context.SaveChangesAsync();

			// Reload detached with Notification populated - exactly what the real Worker's poller
			// query (.Include(q => q.Notification)) hands to a processor running in another scope.
			return await context.Set<FcmQueue>()
				.Include(q => q.Notification)
				.AsNoTracking()
				.SingleAsync(q => q.Id == queue.Id);
		}

		private async Task<int> seedUserSessionAsync(int userId, short statusId, short platformId, string fcmToken)
		{
			await using var context = createContext();
			var session = new UserSession
			{
				UserId = userId,
				StatusId = statusId,
				ClientInfo = new SessionClientInfo
				{
					PlatformId = platformId,
					FCM_FID = fcmToken,
				},
			};
			context.Add(session);
			await context.SaveChangesAsync();
			return session.Id;
		}
	}
}
