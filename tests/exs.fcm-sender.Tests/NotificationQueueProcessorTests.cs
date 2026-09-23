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

		[Fact]
		public async Task ProcessAsync_SuccessfulSend_RecordsTheProviderMessageIdAndNoErrorCode()
		{
			// The message id is the only handle for correlating this row with Firebase's own delivery
			// reporting, which is where any "it never arrived" investigation has to start.
			var queueItem = await seedNotificationAndQueueItemAsync(userId: 1);
			await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Ios, "token-1");
			var stub = new StubFcmMessageSender(FcmSendResult.Ok("projects/x/messages/abc123"));

			await createProcessor(stub).ProcessAsync(queueItem, CancellationToken.None);

			await using var db = createContext();
			var sent = await db.Set<FcmSent>().SingleAsync();
			sent.ProviderMessageId.ShouldBe("projects/x/messages/abc123");
			sent.ErrorCode.ShouldBeNull();
			sent.Error.ShouldBeNull();
		}

		[Fact]
		public async Task ProcessAsync_FailedSend_RecordsTheMachineReadableCodeAlongsideThePinnedProse()
		{
			// Error is free-form English straight from Firebase and changes without notice, so it cannot
			// be grouped on. ErrorCode is what a failure-rate breakdown or a token-cleanup job reads.
			var queueItem = await seedNotificationAndQueueItemAsync(userId: 1);
			await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Ios, "token-1");
			var stub = new StubFcmMessageSender(FcmSendResult.Failed(FcmSendOutcome.PermanentError, "Unregistered", "Requested entity was not found."));

			await createProcessor(stub).ProcessAsync(queueItem, CancellationToken.None);

			await using var db = createContext();
			var sent = await db.Set<FcmSent>().SingleAsync();
			sent.ErrorCode.ShouldBe("Unregistered");
			sent.Error.ShouldBe("Requested entity was not found.");
			sent.ProviderMessageId.ShouldBeNull();
		}

		[Fact]
		public async Task ProcessAsync_NoActiveSessions_RecordsTheInternalCodeNotJustProse()
		{
			// This row is not a Firebase failure at all - nothing was attempted. It still needs a code so
			// that grouping the column gives a complete picture rather than a bucket of nulls.
			var queueItem = await seedNotificationAndQueueItemAsync(userId: 1);

			await createProcessor(new StubFcmMessageSender()).ProcessAsync(queueItem, CancellationToken.None);

			await using var db = createContext();
			var sent = await db.Set<FcmSent>().SingleAsync();
			sent.ErrorCode.ShouldBe(FcmSentErrorCode.NoActiveSessions);
			sent.ProviderMessageId.ShouldBeNull();
		}

		[Fact]
		public async Task ProcessAsync_FcmRejectsTheToken_ClearsItOnThatSession()
		{
			// Without this the device is retried on every future notification forever - a wasted
			// round-trip and a junk fcm_sent row each time - because nothing else ever removes a token
			// that FCM has already said is dead.
			var queueItem = await seedNotificationAndQueueItemAsync(userId: 1);
			var sessionId = await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Ios, "dead-token");
			var stub = new StubFcmMessageSender(FcmSendResult.Failed(FcmSendOutcome.PermanentError, "Unregistered", "app was uninstalled", tokenRejected: true));

			await createProcessor(stub).ProcessAsync(queueItem, CancellationToken.None);

			await using var db = createContext();
			var session = await db.Set<UserSession>().SingleAsync(s => s.Id == sessionId);
			session.ClientInfo.FCM_FID.ShouldBeEmpty();
			// The session itself stays active - the user has not been signed out, they just have no
			// device registered for push any more.
			session.StatusId.ShouldBe(UserSessionStatus.Active);
			// The delivery record still describes what was attempted and against which token.
			var sent = await db.Set<FcmSent>().SingleAsync();
			sent.FcmToken.ShouldBe("dead-token");
			sent.Error.ShouldBe("app was uninstalled");
		}

		[Fact]
		public async Task ProcessAsync_PermanentFailureThatIsNotTheTokensFault_LeavesTheTokenAlone()
		{
			// InvalidArgument and ThirdPartyAuthError are permanent but say nothing about the token -
			// they are a malformed message and a broken APNs credential respectively. Both would fail
			// every send at once, so reaping on them would empty the token of every active session in a
			// single poll pass.
			var queueItem = await seedNotificationAndQueueItemAsync(userId: 1);
			var sessionId = await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Ios, "good-token");
			var stub = new StubFcmMessageSender(FcmSendResult.Failed(FcmSendOutcome.PermanentError, "InvalidArgument", "message payload too large"));

			await createProcessor(stub).ProcessAsync(queueItem, CancellationToken.None);

			await using var db = createContext();
			(await db.Set<UserSession>().SingleAsync(s => s.Id == sessionId)).ClientInfo.FCM_FID.ShouldBe("good-token");
		}

		[Fact]
		public async Task ProcessAsync_SuccessfulSend_LeavesTheTokenAlone()
		{
			var queueItem = await seedNotificationAndQueueItemAsync(userId: 1);
			var sessionId = await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Ios, "good-token");
			var stub = new StubFcmMessageSender(FcmSendResult.Ok("msg-1"));

			await createProcessor(stub).ProcessAsync(queueItem, CancellationToken.None);

			await using var db = createContext();
			(await db.Set<UserSession>().SingleAsync(s => s.Id == sessionId)).ClientInfo.FCM_FID.ShouldBe("good-token");
		}

		[Fact]
		public async Task ProcessAsync_SessionRegisteredAFreshTokenMidSend_DoesNotClearIt()
		{
			// The race the update guards against. Sessions are read at the top of ProcessAsync and queue
			// rows for the same user run in parallel scopes, so by the time a rejection comes back the
			// client may have reinstalled and registered a new token. Clearing by session id alone would
			// throw that new token away; matching on the rejected value too means the update simply
			// matches nothing.
			var queueItem = await seedNotificationAndQueueItemAsync(userId: 1);
			var sessionId = await seedUserSessionAsync(userId: 1, UserSessionStatus.Active, ClientDevicePlatform.Ios, "stale-token");
			var stub = new RegistersNewTokenMidSendFcmMessageSender(this, sessionId, "freshly-registered-token");

			await createProcessor(stub).ProcessAsync(queueItem, CancellationToken.None);

			await using var db = createContext();
			(await db.Set<UserSession>().SingleAsync(s => s.Id == sessionId)).ClientInfo.FCM_FID.ShouldBe("freshly-registered-token");
		}

		/// <summary>
		/// Rewrites the session's token as part of responding, standing in for a client that
		/// re-registered between ProcessAsync reading its sessions and the rejection coming back.
		/// </summary>
		private sealed class RegistersNewTokenMidSendFcmMessageSender : IFcmMessageSender
		{
			public RegistersNewTokenMidSendFcmMessageSender(NotificationQueueProcessorTests owner, int sessionId, string newToken)
			{
				mOwner = owner;
				mSessionId = sessionId;
				mNewToken = newToken;
			}

			public async Task<FcmSendResult> SendAsync(string token, short platformId, string title, string body, short typeId, int? entityId, string payload, CancellationToken cancellationToken)
			{
				await using (var context = mOwner.createContext())
				{
					var session = await context.Set<UserSession>().SingleAsync(s => s.Id == mSessionId, cancellationToken);
					session.ClientInfo.FCM_FID = mNewToken;
					await context.SaveChangesAsync(cancellationToken);
				}
				return FcmSendResult.Failed(FcmSendOutcome.PermanentError, "Unregistered", "the old token is dead", tokenRejected: true);
			}

			private readonly NotificationQueueProcessorTests mOwner;
			private readonly int mSessionId;
			private readonly string mNewToken;
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
