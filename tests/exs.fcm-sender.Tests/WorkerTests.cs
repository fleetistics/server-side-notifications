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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.fcm_sender.Tests
{
	/// <summary>
	/// Drives Worker.pollOnceAsync directly (internal, exposed via InternalsVisibleTo) against a real
	/// SQLite-backed NotificationDatabaseContext through a small DI container - this exercises the
	/// exact "poller scope loads/expires, processor scope sends" split the real Worker performs,
	/// without racing its real polling-interval timing.
	/// </summary>
	public sealed class WorkerTests : IAsyncLifetime
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
		public async Task PollOnceAsync_NoRows_ReturnsFalse()
		{
			var (worker, _) = createWorker(new StubFcmMessageSender());

			var processedAny = await worker.pollOnceAsync(CancellationToken.None);

			processedAny.ShouldBeFalse();
		}

		[Fact]
		public async Task PollOnceAsync_FreshRow_SendsAndDrainsQueue()
		{
			await seedQueueItemAsync(userId: 1, age: TimeSpan.FromMinutes(1));
			await seedUserSessionAsync(userId: 1, "fresh-token");
			var stub = new StubFcmMessageSender(FcmSendResult.Ok("msg"));
			var (worker, _) = createWorker(stub);

			var processedAny = await worker.pollOnceAsync(CancellationToken.None);

			processedAny.ShouldBeTrue();
			stub.Calls.Count.ShouldBe(1);
			stub.Calls[0].Token.ShouldBe("fresh-token");
			await using var db = createContext();
			(await db.Set<FcmQueue>().AnyAsync()).ShouldBeFalse();
		}

		[Fact]
		public async Task PollOnceAsync_ExpiredRow_IsNeverSentAndIsRecordedAsExpired()
		{
			// Regression guard: expired rows were being computed but never excluded from the batch
			// handed to the parallel send loop, so an "expired" notification still got pushed.
			await seedQueueItemAsync(userId: 1, age: TimeSpan.FromHours(2));
			await seedUserSessionAsync(userId: 1, "expired-token");
			var stub = new StubFcmMessageSender(FcmSendResult.Ok("msg"));
			var (worker, _) = createWorker(stub);

			await worker.pollOnceAsync(CancellationToken.None);

			stub.Calls.ShouldBeEmpty(); // never sent, regardless of an active session existing
			await using var db = createContext();
			var sent = await db.Set<FcmSent>().SingleAsync();
			sent.Error.ShouldBe("Expired");
			sent.ErrorCode.ShouldBe(FcmSentErrorCode.Expired); // groupable, unlike the prose above
			sent.ProviderMessageId.ShouldBeNull();
			(await db.Set<FcmQueue>().AnyAsync()).ShouldBeFalse();
		}

		[Fact]
		public async Task PollOnceAsync_ExpiryWindowComesFromConfiguration()
		{
			// The delivery window used to be a hardcoded 30 minutes here and another hardcoded 30 minutes
			// in NotificationService, in a different assembly. This row is two hours old, so it would be
			// expired under the default and must not be under a widened one.
			await seedQueueItemAsync(userId: 1, age: TimeSpan.FromHours(2));
			await seedUserSessionAsync(userId: 1, "token-1");
			var stub = new StubFcmMessageSender(FcmSendResult.Ok("msg"));
			var (worker, _) = createWorker(stub, queueExpirationMinutes: 24 * 60);

			await worker.pollOnceAsync(CancellationToken.None);

			stub.Calls.Count.ShouldBe(1); // sent, not expired
			await using var db = createContext();
			(await db.Set<FcmSent>().SingleAsync()).ErrorCode.ShouldBeNull();
		}

		[Fact]
		public async Task PollOnceAsync_MixOfFreshAndExpiredRows_OnlySendsToTheFreshOne()
		{
			await seedQueueItemAsync(userId: 1, age: TimeSpan.FromHours(2));
			await seedUserSessionAsync(userId: 1, "expired-token");
			await seedQueueItemAsync(userId: 2, age: TimeSpan.FromMinutes(1));
			await seedUserSessionAsync(userId: 2, "fresh-token");
			var stub = new StubFcmMessageSender(FcmSendResult.Ok("msg"));
			var (worker, _) = createWorker(stub);

			await worker.pollOnceAsync(CancellationToken.None);

			stub.Calls.Count.ShouldBe(1);
			stub.Calls[0].Token.ShouldBe("fresh-token");
			await using var db = createContext();
			(await db.Set<FcmQueue>().AnyAsync()).ShouldBeFalse();
			(await db.Set<FcmSent>().CountAsync()).ShouldBe(2); // one "Expired", one real send result
		}

		[Fact]
		public async Task PollOnceAsync_MoreRowsThanBatchSize_OnlyClaimsUpToBatchSize()
		{
			for (var userId = 1; userId <= 5; userId++)
			{
				await seedQueueItemAsync(userId, age: TimeSpan.FromMinutes(1));
				await seedUserSessionAsync(userId, $"token-{userId}");
			}
			var stub = new StubFcmMessageSender(FcmSendResult.Ok("msg"));
			var (worker, _) = createWorker(stub, batchSize: 2);

			var processedAny = await worker.pollOnceAsync(CancellationToken.None);

			processedAny.ShouldBeTrue();
			stub.Calls.Count.ShouldBe(2);
			await using var db = createContext();
			(await db.Set<FcmQueue>().CountAsync()).ShouldBe(3); // the rest stay queued for the next poll
		}

		private (Worker Worker, IServiceProvider Provider) createWorker(IFcmMessageSender sender, int batchSize = 200, int maxAttempts = 1, int queueExpirationMinutes = 30)
		{
			var options = new FcmSenderOptions
			{
				BatchSize = batchSize,
				QueueExpirationMinutes = queueExpirationMinutes,
				// Sequential on purpose: every scope shares one SQLite in-memory connection (via
				// mConnection), which doesn't support concurrent commands the way production's
				// per-scope Npgsql connections do. These tests aren't asserting anything about
				// concurrency itself, so keep it out of the picture rather than fighting it.
				MaxConcurrency = 1,
				MaxAttemptsPerSession = maxAttempts,
				RetryBaseDelayMilliseconds = 1,
			};

			var services = new ServiceCollection();
			// NotificationsModelBuilder/CommonModelBuilder memoize "model already built" behind a
			// process-wide static bool, not per-model-cache-key - so this must construct
			// DbContextOptions the exact same way createContext() does everywhere else in these
			// tests. Microsoft.Extensions.DependencyInjection's AddDbContext wires its own internal
			// EF service provider into the options, which is a different model cache key and would
			// hit that guard already tripped, producing a context with an empty model.
			services.AddScoped(_ => createContext());
			services.AddScoped<IRepository>(sp => new EntityFrameworkRepository<NotificationDatabaseContext>(sp.GetRequiredService<NotificationDatabaseContext>()));
			services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
			services.AddSingleton(sender);
			services.AddSingleton(Options.Create(options));
			services.AddSingleton<ResilientFcmSender>();
			services.AddScoped<NotificationQueueProcessor>();

			var provider = services.BuildServiceProvider();
			var worker = new Worker(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options), NullLogger<Worker>.Instance);
			return (worker, provider);
		}

		private NotificationDatabaseContext createContext() =>
			new(new DbContextOptionsBuilder<NotificationDatabaseContext>().UseSqlite(mConnection).Options);

		private async Task seedQueueItemAsync(int userId, TimeSpan age)
		{
			await using var context = createContext();
			var notification = new Notification
			{
				Date = DateTime.UtcNow,
				TypeId = 1,
				Title = "Title",
				Body = "Body",
				Payload = "",
			};
			context.Add(notification);
			await context.SaveChangesAsync();

			context.Add(new FcmQueue
			{
				NotificationId = notification.Id,
				UserId = userId,
				Date = DateTime.UtcNow - age,
			});
			await context.SaveChangesAsync();
		}

		private async Task seedUserSessionAsync(int userId, string fcmToken)
		{
			await using var context = createContext();
			context.Add(new UserSession
			{
				UserId = userId,
				StatusId = UserSessionStatus.Active,
				ClientInfo = new SessionClientInfo
				{
					PlatformId = ClientDevicePlatform.Ios,
					FCM_FID = fcmToken,
				},
			});
			await context.SaveChangesAsync();
		}
	}
}
