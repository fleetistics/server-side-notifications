using System.Collections.Concurrent;
using exs.Database.Commons.Impl;
using exs.Database.Commons.Interfaces;
using exs.modelCommons.AppStructure;
using exs.modelCommons.UserManagement;
using exs.notifications_contracts;
using exs.notifications_model.Notifications;
using exs.notifications_model.Outbox;
using exs.notifications_service.Impl;
using exs.notifications_service.Interfaces;
using exs.notifications_service.Sender;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.notifications_database.Tests.SenderMode
{
	/// <summary>
	/// The host-side sender-mode code, unchanged, against a real PostgreSQL. The unit tests prove the logic on
	/// SQLite; this proves the queries it runs (and the timestamps it writes) are ones Npgsql accepts.
	/// </summary>
	public sealed class PostgresSenderModeTests : IClassFixture<PostgresFixture>, IAsyncLifetime
	{
		private readonly PostgresFixture mDatabase;
		private readonly FakeTransport mTransport = new();
		private readonly List<IAsyncDisposable> mCleanup = new();

		public PostgresSenderModeTests(PostgresFixture database) => mDatabase = database;

		public async ValueTask InitializeAsync() => await mDatabase.ResetAsync();

		public async ValueTask DisposeAsync()
		{
			foreach (var item in Enumerable.Reverse(mCleanup)) await item.DisposeAsync();
		}

		private ServiceProvider provider()
		{
			var services = new ServiceCollection();
			services.AddScoped(_ => mDatabase.CreateContext());
			services.AddScoped<IRepository>(p => new EntityFrameworkRepository<NotificationDatabaseContext>(p.GetRequiredService<NotificationDatabaseContext>()));
			services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
			var built = services.BuildServiceProvider();
			mCleanup.Add(built);
			return built;
		}

		private static SenderTransportOptions options(Action<SenderTransportOptions>? configure = null)
		{
			var result = new SenderTransportOptions { BaseUrl = "http://sender.test", ApiKey = "k", RelayPollIntervalSeconds = 60, ResultPageSize = 100 };
			configure?.Invoke(result);
			return result;
		}

		private async Task<Notification> addNotificationAsync(short typeId = 1, int? entityId = null, string? payload = null, byte transportType = NotificationTransportType.FCM)
		{
			await using var context = mDatabase.CreateContext();
			var notification = new Notification { Date = DateTime.UtcNow, TypeId = typeId, EntityId = entityId, Title = "Title", Body = "Body", Payload = payload, TransportType = transportType };
			context.Add(notification);
			await context.SaveChangesAsync();
			return notification;
		}

		private async Task<OutboxMessage> addOutboxAsync(byte state = OutboxState.Pending, int userId = 1, DateTime? completedAt = null, string? @ref = null, string channel = "fcm")
		{
			var notification = await addNotificationAsync();
			await using var context = mDatabase.CreateContext();
			var row = new OutboxMessage
			{
				MessageId = Guid.NewGuid(),
				NotificationId = notification.Id,
				UserId = userId,
				Channel = channel,
				Ref = @ref,
				Envelope = state == OutboxState.Pending ? """{"to":"x"}""" : null,
				CreatedAt = DateTime.UtcNow,
				ExpiresAt = DateTime.UtcNow.AddMinutes(30),
				State = state,
				CompletedAt = completedAt,
			};
			context.Add(row);
			await context.SaveChangesAsync();
			return row;
		}

		private async Task<int> addSessionAsync(int userId, string token, short platform = ClientDevicePlatform.Android, short status = UserSessionStatus.Active, string appUid = "app")
		{
			await using var context = mDatabase.CreateContext();
			var session = new UserSession
			{
				UserId = userId,
				StatusId = status,
				ClientInfo = new SessionClientInfo { FCM_FID = token, PlatformId = platform, AppUID = appUid },
			};
			context.Add(session);
			await context.SaveChangesAsync();
			return session.Id;
		}

		private static async Task eventuallyAsync(Func<Task<bool>> condition, string what)
		{
			var deadline = DateTime.UtcNow.AddSeconds(20);
			while (DateTime.UtcNow < deadline)
			{
				if (await condition()) return;
				await Task.Delay(50);
			}
			throw new TimeoutException($"Timed out waiting for: {what}");
		}

		private static ResultEvent result(Guid messageId, long resultId, string status = ResultStatuses.Sent, bool destinationInvalid = false, string? destination = "tok") => new()
		{
			SchemaVersion = ContractVersion.Current,
			ResultId = resultId,
			MessageId = messageId,
			Channel = "fcm",
			Status = status,
			Destination = destination,
			DestinationInvalid = destinationInvalid,
			ErrorKind = destinationInvalid ? ErrorKinds.DestinationInvalid : null,
			ErrorCode = destinationInvalid ? "Unregistered" : null,
			ProviderMessageId = destinationInvalid ? null : "projects/p/messages/1",
			Attempts = 1,
			CompletedAt = new DateTimeOffset(2026, 10, 17, 12, 0, 5, TimeSpan.Zero),
		};

		[Fact]
		public async Task TheFanOutWritesItsOutboxRows_ToPostgres()
		{
			await addSessionAsync(1, "tok-a", ClientDevicePlatform.Android);
			await addSessionAsync(1, "tok-b", ClientDevicePlatform.Ios);
			await addSessionAsync(1, "tok-gone", status: UserSessionStatus.LoggedOutByUser);
			var notification = await addNotificationAsync(typeId: 3, entityId: 55, payload: """{ "badge": 2 }""");
			var processor = new FcmOutboxProcessor(
				new ServiceCollection().AddSingleton<IFcmDestinationResolver>(new UserSessionFcmDestinationResolver(Options.Create(options()))).BuildServiceProvider(),
				Options.Create(options()), TimeProvider.System, NullLogger<FcmOutboxProcessor>.Instance);

			using (var repository = new EntityFrameworkRepository<NotificationDatabaseContext>(mDatabase.CreateContext()))
			{
				await processor.ProcessNotificationAsync(repository, notification.Id, [1, 2], CancellationToken.None);
				await repository.SaveAsync();
			}

			var rows = await mDatabase.QueryAsync(db => db.Set<OutboxMessage>().AsNoTracking().OrderBy(o => o.Id).ToListAsync());
			rows.Count(r => r.UserId == 1 && r.State == OutboxState.Pending).ShouldBe(2);
			rows.Single(r => r.UserId == 2).ErrorKind.ShouldBe(OutboxErrorKind.NoDestination);
			rows.ShouldAllBe(r => r.CreatedAt.Kind == DateTimeKind.Utc && r.ExpiresAt >= r.CreatedAt);

			// A second pass over the same notification (the sweep after a crash) writes nothing more.
			using (var repository = new EntityFrameworkRepository<NotificationDatabaseContext>(mDatabase.CreateContext()))
			{
				await processor.ProcessNotificationAsync(repository, notification.Id, [1, 2], CancellationToken.None);
				await repository.SaveAsync();
			}
			(await mDatabase.QueryAsync(db => db.Set<OutboxMessage>().CountAsync())).ShouldBe(3);
		}

		[Fact]
		public async Task TheRelay_SubmitsEveryPendingRow_ThroughPostgresQueries()
		{
			for (var i = 0; i < 12; i++) await addOutboxAsync();
			var relay = new OutboxRelay(provider().GetRequiredService<IServiceScopeFactory>(), mTransport, Options.Create(options(o => { o.RelayBatchSize = 5; o.RelayMaxConcurrency = 3; })),
				new OutboxSignal(), TimeProvider.System, NullLogger<OutboxRelay>.Instance);
			mCleanup.Add(new Stopper(relay));

			await relay.StartAsync(CancellationToken.None);

			await eventuallyAsync(async () => await mDatabase.QueryAsync(db => db.Set<OutboxMessage>().CountAsync(o => o.State == OutboxState.Submitted)) == 12,
				"all 12 rows to be submitted through 5-row batches");
			mTransport.Submitted.Count.ShouldBe(12);
			(await mDatabase.QueryAsync(db => db.Set<OutboxMessage>().AllAsync(o => o.Envelope == null && o.SubmittedAt != null))).ShouldBeTrue();
		}

		[Fact]
		public async Task AnEmailRelay_SubmitsOnlyEmailRows_AndNeverTheOtherChannelsBacklog_OnPostgres()
		{
			for (var i = 0; i < 8; i++) await addOutboxAsync(channel: "fcm");
			for (var i = 0; i < 6; i++) await addOutboxAsync(channel: "email");
			var relay = new OutboxRelay(provider().GetRequiredService<IServiceScopeFactory>(), mTransport, Options.Create(options(o => { o.RelayBatchSize = 5; o.RelayMaxConcurrency = 3; })),
				new OutboxSignal(), TimeProvider.System, NullLogger<OutboxRelay>.Instance, "email");
			mCleanup.Add(new Stopper(relay));

			await relay.StartAsync(CancellationToken.None);

			await eventuallyAsync(async () => await mDatabase.QueryAsync(db => db.Set<OutboxMessage>().CountAsync(o => o.Channel == "email" && o.State == OutboxState.Submitted)) == 6,
				"all 6 email rows to be submitted");
			mTransport.Submitted.Count.ShouldBe(6);
			(await mDatabase.QueryAsync(db => db.Set<OutboxMessage>().CountAsync(o => o.Channel == "fcm" && o.State == OutboxState.Pending))).ShouldBe(8);
		}

		[Fact]
		public async Task TheEmailProcessor_WritesItsRows_AlongsideThePushRowsOfTheSameNotification_OnPostgres()
		{
			var notification = await addNotificationAsync(typeId: 3);
			var services = new ServiceCollection()
				.AddSingleton<IEmailDestinationResolver>(new FixedEmailResolver(new EmailResolvedDestination(1, "sam@example.com", "Sam", Ref: "u:1")))
				.BuildServiceProvider();
			var processor = new EmailOutboxProcessor(services, Options.Create(options()), TimeProvider.System, NullLogger<EmailOutboxProcessor>.Instance);
			await addOutboxForAsync(notification.Id, "fcm");

			for (var pass = 0; pass < 2; pass++) // the second is the sweep after a crash
			{
				using var repository = new EntityFrameworkRepository<NotificationDatabaseContext>(mDatabase.CreateContext());
				await processor.ProcessNotificationAsync(repository, notification.Id, [1, 2], CancellationToken.None);
				await repository.SaveAsync();
			}

			var rows = await mDatabase.QueryAsync(db => db.Set<OutboxMessage>().AsNoTracking().Where(o => o.Channel == "email").OrderBy(o => o.UserId).ToListAsync());
			rows.Count.ShouldBe(2);
			rows[0].State.ShouldBe(OutboxState.Pending);
			rows[1].ErrorCode.ShouldBe(OutboxErrorCode.NoEmailAddress);
			(await mDatabase.QueryAsync(db => db.Set<OutboxMessage>().CountAsync(o => o.Channel == "fcm"))).ShouldBe(1);
		}

		[Fact]
		public async Task AFreeFormEmail_GoesFromTheWriterThroughTheRelayAndThePuller_WithNoNotificationAndNoUser_OnPostgres()
		{
			var monitor = new ServiceCollection().Configure<SenderTransportOptions>("email", o => { o.BaseUrl = "http://mail.test"; o.ApiKey = "k"; }).BuildServiceProvider()
				.GetRequiredService<IOptionsMonitor<SenderTransportOptions>>();
			OutboxMessage written;
			using (var repository = new EntityFrameworkRepository<NotificationDatabaseContext>(mDatabase.CreateContext()))
			{
				written = new EmailOutboxWriter(monitor, TimeProvider.System).Write(repository, new EmailMessage { To = "pat@example.com", Subject = "Reset", Text = "https://example.com/reset?token=abc" });
				await repository.SaveAsync();
			}
			written.Id.ShouldNotBe(0);

			// A notification's own email fan-out, running next to it, must cope with a table that now holds rows with no user.
			var notification = await addNotificationAsync(typeId: 3);
			var services = new ServiceCollection()
				.AddSingleton<IEmailDestinationResolver>(new FixedEmailResolver(new EmailResolvedDestination(1, "sam@example.com")))
				.BuildServiceProvider();
			using (var repository = new EntityFrameworkRepository<NotificationDatabaseContext>(mDatabase.CreateContext()))
			{
				await new EmailOutboxProcessor(services, Options.Create(options()), TimeProvider.System, NullLogger<EmailOutboxProcessor>.Instance)
					.ProcessNotificationAsync(repository, notification.Id, [1], CancellationToken.None);
				await repository.SaveAsync();
			}

			var relay = new OutboxRelay(provider().GetRequiredService<IServiceScopeFactory>(), mTransport, Options.Create(options()), new OutboxSignal(), TimeProvider.System, NullLogger<OutboxRelay>.Instance, "email");
			mCleanup.Add(new Stopper(relay));
			await relay.StartAsync(CancellationToken.None);
			await eventuallyAsync(async () => await mDatabase.QueryAsync(db => db.Set<OutboxMessage>().CountAsync(o => o.State == OutboxState.Submitted)) == 2, "both email rows to be submitted");

			mTransport.Page = new ResultPage
			{
				NextAfter = 5,
				Results = [result(written.MessageId, 5, ResultStatuses.Failed, destinationInvalid: true, destination: "pat@example.com") with { Channel = "email" }],
			};
			var puller = new ResultPuller(provider().GetRequiredService<IServiceScopeFactory>(), mTransport, new ServiceCollection().BuildServiceProvider(),
				Options.Create(options()), TimeProvider.System, NullLogger<ResultPuller>.Instance, "email");
			await puller.PullOnceAsync(CancellationToken.None);

			var rows = await mDatabase.QueryAsync(db => db.Set<OutboxMessage>().AsNoTracking().OrderBy(o => o.Id).ToListAsync());
			var freeForm = rows.Single(r => r.Id == written.Id);
			freeForm.NotificationId.ShouldBeNull();
			freeForm.UserId.ShouldBeNull();
			freeForm.State.ShouldBe(OutboxState.Completed);
			freeForm.DestinationInvalid.ShouldBeTrue();
			freeForm.Envelope.ShouldBeNull();
			rows.Single(r => r.Id != written.Id).State.ShouldBe(OutboxState.Submitted); // the other mail is still waiting for its result
			(await mDatabase.QueryAsync(db => db.Set<Notification>().CountAsync())).ShouldBe(1); // only the notification above: CreateEmail wrote none
		}

		[Fact]
		public async Task TheSweep_GivesEachNotificationToTheProcessorsOfItsOwnTransport_OnPostgres()
		{
			var push = await addNotificationAsync(typeId: 3);
			var mail = await addNotificationAsync(typeId: 3, transportType: NotificationTransportType.Email);
			await using (var context = mDatabase.CreateContext())
			{
				context.AddRange(new NotificationToUser { NotificationId = push.Id, UserId = 1 }, new NotificationToUser { NotificationId = mail.Id, UserId = 2 });
				await context.SaveChangesAsync();
			}
			var pushProcessor = new RecordingProcessor(NotificationTransportType.FCM);
			var mailProcessor = new RecordingProcessor(NotificationTransportType.Email);
			var service = new NotificationService(provider().GetRequiredService<IServiceScopeFactory>(), [pushProcessor, mailProcessor],
				Options.Create(new exs.notifications_service.NotificationServiceOptions { SweepGracePeriodSeconds = 0 }), NullLogger<NotificationService>.Instance);

			await service.processNotificationsTable(CancellationToken.None);

			pushProcessor.Calls.ShouldBe([push.Id]);
			mailProcessor.Calls.ShouldBe([mail.Id]);
			(await mDatabase.QueryAsync(db => db.Set<Notification>().AsNoTracking().ToListAsync())).ShouldAllBe(n => n.Status == NotificationStatus.Processed);
		}

		private sealed class RecordingProcessor : ITransportNotificationProcessor
		{
			public RecordingProcessor(byte transportType) => TransportType = transportType;

			public byte TransportType { get; }

			public List<int> Calls { get; } = new();

			public Task ProcessNotificationAsync(IRepository repository, int notificationId, List<int> usersIds, CancellationToken cancellationToken)
			{
				Calls.Add(notificationId);
				return Task.CompletedTask;
			}
		}

		private sealed class FixedEmailResolver : IEmailDestinationResolver
		{
			private readonly EmailResolvedDestination[] mDestinations;

			public FixedEmailResolver(params EmailResolvedDestination[] destinations) => mDestinations = destinations;

			public Task<IReadOnlyList<EmailResolvedDestination>> ResolveAsync(IRepository repository, Notification notification, IReadOnlyCollection<int> userIds, CancellationToken cancellationToken) =>
				Task.FromResult<IReadOnlyList<EmailResolvedDestination>>(mDestinations.Where(d => userIds.Contains(d.UserId)).ToList());
		}

		private async Task addOutboxForAsync(int notificationId, string channel)
		{
			await using var context = mDatabase.CreateContext();
			context.Add(new OutboxMessage
			{
				MessageId = Guid.NewGuid(), NotificationId = notificationId, UserId = 1, Channel = channel, Envelope = "{}",
				CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(30), State = OutboxState.Pending,
			});
			await context.SaveChangesAsync();
		}

		[Fact]
		public async Task TheRelayPrunesOnlyOldFinishedRows_WithABulkDeleteThatRunsOnPostgres()
		{
			var old = DateTime.UtcNow.AddDays(-40);
			var oldDone = await addOutboxAsync(OutboxState.Completed, completedAt: old);
			var oldRejected = await addOutboxAsync(OutboxState.Rejected, completedAt: old);
			var recent = await addOutboxAsync(OutboxState.Completed, completedAt: DateTime.UtcNow.AddDays(-1));
			var pending = await addOutboxAsync(OutboxState.Pending);
			var submitted = await addOutboxAsync(OutboxState.Submitted);
			var relay = new OutboxRelay(provider().GetRequiredService<IServiceScopeFactory>(), mTransport, Options.Create(options(o => o.OutboxRetentionDays = 30)),
				new OutboxSignal(), TimeProvider.System, NullLogger<OutboxRelay>.Instance);

			var removed = await relay.PruneOnceAsync(CancellationToken.None);

			removed.ShouldBe(2);
			var remaining = await mDatabase.QueryAsync(db => db.Set<OutboxMessage>().Select(o => o.Id).ToListAsync());
			remaining.ShouldBe([recent.Id, pending.Id, submitted.Id], ignoreOrder: true);
			remaining.ShouldNotContain(oldDone.Id);
			remaining.ShouldNotContain(oldRejected.Id);
		}

		[Fact]
		public async Task ThePuller_AppliesAPageOfResults_AndKeepsItsCursor_OnPostgres()
		{
			var rows = new List<OutboxMessage>();
			for (var i = 0; i < 4; i++) rows.Add(await addOutboxAsync(OutboxState.Submitted, userId: i + 1));
			var dead = rows[1];
			mTransport.Page = new ResultPage
			{
				NextAfter = 9,
				Results = [
					result(rows[0].MessageId, 6),
					result(dead.MessageId, 7, ResultStatuses.Failed, destinationInvalid: true, destination: "tok-dead"),
					result(rows[2].MessageId, 8),
					result(Guid.NewGuid(), 9), // not this host's
				],
			};
			var invalidator = new RecordingInvalidator();
			var puller = new ResultPuller(provider().GetRequiredService<IServiceScopeFactory>(), mTransport,
				new ServiceCollection().AddSingleton<IFcmTokenInvalidator>(invalidator).BuildServiceProvider(),
				Options.Create(options()), TimeProvider.System, NullLogger<ResultPuller>.Instance);

			await puller.PullOnceAsync(CancellationToken.None);
			mTransport.Page = new ResultPage { NextAfter = 9, Results = [] };
			await puller.PullOnceAsync(CancellationToken.None);

			var states = await mDatabase.QueryAsync(db => db.Set<OutboxMessage>().AsNoTracking().OrderBy(o => o.Id).ToListAsync());
			states.Select(s => s.State).ShouldBe([OutboxState.Completed, OutboxState.Completed, OutboxState.Completed, OutboxState.Submitted]);
			states[1].DestinationInvalid.ShouldBeTrue();
			states[1].ErrorCode.ShouldBe("Unregistered");
			states[0].CompletedAt.ShouldBe(new DateTime(2026, 10, 17, 12, 0, 5, DateTimeKind.Utc));
			invalidator.Calls.ShouldHaveSingleItem().ShouldBe((2, "tok-dead"));
			(await mDatabase.QueryAsync(db => db.Set<TransportCursor>().SingleAsync())).Position.ShouldBe(9);
			mTransport.FetchPositions.ShouldBe([0L, 9L]); // the second pull resumed from the stored cursor
		}

		[Fact]
		public async Task TheUserSessionHooks_ResolveAndClear_OnPostgres()
		{
			var android = await addSessionAsync(1, "tok-android", ClientDevicePlatform.Android, appUid: "com.fleetistics.mf5");
			await addSessionAsync(1, "tok-ios", ClientDevicePlatform.Ios);
			await addSessionAsync(1, "tok-windows", ClientDevicePlatform.Windows);
			await addSessionAsync(1, "tok-out", status: UserSessionStatus.LoggedOutByUser);
			await addSessionAsync(1, "tok-android", ClientDevicePlatform.Android); // the same device again, newer session
			await addSessionAsync(2, "tok-other-user");
			var resolver = new UserSessionFcmDestinationResolver(Options.Create(options(o => o.AccountsByAppId["com.fleetistics.mf5"] = "mf5")));

			IReadOnlyList<FcmResolvedDestination> destinations;
			using (var repository = new EntityFrameworkRepository<NotificationDatabaseContext>(mDatabase.CreateContext()))
			{
				destinations = await resolver.ResolveAsync(repository, new Notification { Id = 1 }, [1], CancellationToken.None);
			}

			destinations.Select(d => d.Token).ShouldBe(["tok-android", "tok-ios"], ignoreOrder: true); // one push per device, none for the rest
			destinations.Single(d => d.Token == "tok-android").Ref.ShouldNotBe($"s:{android}"); // the newest session wins

			var invalidator = new UserSessionFcmTokenInvalidator(NullLogger<UserSessionFcmTokenInvalidator>.Instance);
			bool cleared;
			using (var repository = new EntityFrameworkRepository<NotificationDatabaseContext>(mDatabase.CreateContext()))
			{
				cleared = await invalidator.InvalidateAsync(repository, 1, null, "tok-android", CancellationToken.None);
			}

			cleared.ShouldBeTrue();
			var tokens = await mDatabase.QueryAsync(db => db.Set<UserSession>().AsNoTracking().ToListAsync());
			tokens.Where(s => s.UserId == 1 && s.ClientInfo.PlatformId == ClientDevicePlatform.Android && s.StatusId == UserSessionStatus.Active)
				.ShouldAllBe(s => s.ClientInfo.FCM_FID == ""); // every session of that user holding the dead token
			tokens.Single(s => s.ClientInfo.FCM_FID == "tok-ios").StatusId.ShouldBe(UserSessionStatus.Active);
			tokens.Single(s => s.ClientInfo.FCM_FID == "tok-other-user").UserId.ShouldBe(2); // untouched
		}

		private sealed class Stopper : IAsyncDisposable
		{
			private readonly Microsoft.Extensions.Hosting.IHostedService mService;

			public Stopper(Microsoft.Extensions.Hosting.IHostedService service) => mService = service;

			public async ValueTask DisposeAsync() => await mService.StopAsync(CancellationToken.None);
		}

		private sealed class RecordingInvalidator : IFcmTokenInvalidator
		{
			public ConcurrentQueue<(int UserId, string Destination)> Queue { get; } = new();

			public IReadOnlyList<(int UserId, string Destination)> Calls => Queue.ToArray();

			public Task<bool> InvalidateAsync(IRepository repository, int userId, string? @ref, string destination, CancellationToken cancellationToken)
			{
				Queue.Enqueue((userId, destination));
				return Task.FromResult(true);
			}
		}

		private sealed class FakeTransport : INotificationTransport
		{
			private readonly ConcurrentQueue<string> mSubmitted = new();
			private readonly ConcurrentQueue<long> mPositions = new();

			public IReadOnlyList<string> Submitted => mSubmitted.ToArray();

			public IReadOnlyList<long> FetchPositions => mPositions.ToArray();

			public ResultPage Page { get; set; } = new() { Results = [], NextAfter = 0 };

			public Task<SubmitResult> SubmitAsync(string envelopeJson, CancellationToken cancellationToken)
			{
				mSubmitted.Enqueue(envelopeJson);
				return Task.FromResult(new SubmitResult(SubmitOutcome.Accepted));
			}

			public Task<ResultPage> FetchResultsAsync(long after, int limit, CancellationToken cancellationToken)
			{
				mPositions.Enqueue(after);
				return Task.FromResult(Page);
			}
		}
	}
}
