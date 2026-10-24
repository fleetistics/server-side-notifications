using exs.Database.Commons.Interfaces;
using exs.notifications_contracts;
using exs.notifications_model.Notifications;
using exs.notifications_model.Outbox;
using exs.notifications_model.Transports;
using exs.notifications_service.Impl;
using exs.notifications_service.Interfaces;
using exs.notifications_service.Sender;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.notifications_service.Tests.Sender
{
	/// <summary>
	/// The real NotificationService with both transports on: a notification goes out on the one channel it was created
	/// for and on no other, whichever transports the host has. Only the relays and pullers are left out, so the rows can
	/// be looked at before anything submits them.
	/// </summary>
	public sealed class MultiTransportFanOutTests : IAsyncDisposable
	{
		private readonly HostTestDatabase mDatabase = HostTestDatabase.Create();
		private readonly FakeFcmDestinationResolver mPush = new();
		private readonly FakeEmailDestinationResolver mMail = new();
		private readonly ServiceProvider mProvider;
		private readonly NotificationService mService;

		public MultiTransportFanOutTests()
		{
			var values = new Dictionary<string, string?>
			{
				["NotificationService:FcmTransport"] = "Sender",
				["NotificationService:FcmSender:BaseUrl"] = "http://push.test",
				["NotificationService:FcmSender:ApiKey"] = "k",
				["NotificationService:EmailTransport"] = "Sender",
				["NotificationService:EmailSender:BaseUrl"] = "http://mail.test",
				["NotificationService:EmailSender:ApiKey"] = "k",
				["NotificationService:SweepIntervalSeconds"] = "3600",
				["NotificationService:SweepGracePeriodSeconds"] = "0", // so a sweep finds what a test has just written
			};
			var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

			var services = new ServiceCollection();
			mDatabase.Register(services);
			services.AddNotificationSender(configuration);
			services.AddSingleton<IFcmDestinationResolver>(mPush);
			services.AddSingleton<IFcmTokenInvalidator, FakeFcmTokenInvalidator>();
			services.AddSingleton<IEmailDestinationResolver>(mMail);
			services.RemoveAll<IHostedService>(); // only the fan-out is started, by hand
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

		private Task<List<Notification>> notificationsAsync() => mDatabase.QueryAsync(db => db.Set<Notification>().AsNoTracking().OrderBy(n => n.Id).ToListAsync());

		private async Task raiseAsync(Action<INotificationSender, IRepository> create)
		{
			using var scope = mProvider.CreateScope();
			var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
			create(scope.ServiceProvider.GetRequiredService<INotificationSender>(), repository);
			await repository.SaveAsync();
		} // disposing the scope hands the notification to the fan-out

		private Task raiseMobileAsync(short type, params int[] users) =>
			raiseAsync((sender, repository) => sender.CreateMobileNotification(repository, type, 1, "Title", "Body", "{}", users));

		private Task raiseEmailAsync(short type, params int[] users) =>
			raiseAsync((sender, repository) => sender.CreateEmailNotification(repository, type, 1, "Title", "Body", "{}", users));

		private async Task<int> addUnprocessedAsync(byte? transportType, params int[] users)
		{
			// Written the way another process (or a crash between commit and enqueue) leaves it: a New row for the sweep.
			var notification = await mDatabase.AddNotificationAsync(typeId: 3, transportType: transportType ?? NotificationTransportType.FCM);
			await using var context = mDatabase.CreateContext();
			foreach (var user in users) context.Set<NotificationToUser>().Add(new NotificationToUser { NotificationId = notification.Id, UserId = user });
			await context.SaveChangesAsync();
			await Task.Delay(50);
			return notification.Id;
		}

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

		private Task allProcessedAsync(int count) => eventuallyAsync(async () =>
		{
			var all = await notificationsAsync();
			return all.Count == count && all.All(n => n.Status == NotificationStatus.Processed);
		}, $"{count} notification(s) to be marked processed");

		[Fact]
		public async Task AMobileAndAnEmailNotification_EachGoOutOnTheirOwnChannelOnly_AndAreMarkedProcessed()
		{
			mPush.Destinations.Add(new FcmResolvedDestination(7, "tok-a", FcmPlatforms.Android));
			mMail.Destinations.Add(new EmailResolvedDestination(7, "sam@example.com", "Sam"));
			await mService.StartAsync(CancellationToken.None);
			await Task.Delay(100);

			await raiseMobileAsync(3, 7);
			await raiseEmailAsync(3, 7);

			await allProcessedAsync(2);
			var rows = await rowsAsync();
			rows.Count.ShouldBe(2); // one push row for the mobile notification, one email row for the other: neither was done twice
			var push = rows.Single(r => r.Channel == "fcm");
			var mail = rows.Single(r => r.Channel == "email");
			push.State.ShouldBe(OutboxState.Pending);
			mail.State.ShouldBe(OutboxState.Pending);
			var notifications = await notificationsAsync();
			push.NotificationId.ShouldBe(notifications.Single(n => n.TransportType == NotificationTransportType.FCM).Id);
			mail.NotificationId.ShouldBe(notifications.Single(n => n.TransportType == NotificationTransportType.Email).Id);
			ContractJson.Deserialize<MessageEnvelope<EmailBody>>(mail.Envelope!)!.Body.To.Address.ShouldBe("sam@example.com");
			mPush.Calls.ShouldBe(1);
			mMail.Calls.ShouldBe(1);
		}

		[Fact]
		public async Task AMobileNotification_IsOnlyPushed_EvenThoughEmailIsOn_AndTheAddressBookIsNeverAsked()
		{
			mPush.Destinations.Add(new FcmResolvedDestination(7, "tok-a", FcmPlatforms.Android));
			mMail.Destinations.Add(new EmailResolvedDestination(7, "sam@example.com"));
			await mService.StartAsync(CancellationToken.None);
			await Task.Delay(100);

			await raiseMobileAsync(3, 7);

			await allProcessedAsync(1);
			(await rowsAsync()).ShouldHaveSingleItem().Channel.ShouldBe("fcm");
			mMail.Calls.ShouldBe(0);
		}

		[Fact]
		public async Task AnEmailNotification_IsOnlyEmailed_EvenThoughPushIsOn_AndNoDeviceIsLookedUp()
		{
			mPush.Destinations.Add(new FcmResolvedDestination(7, "tok-a", FcmPlatforms.Android));
			mMail.Destinations.Add(new EmailResolvedDestination(7, "sam@example.com"));
			await mService.StartAsync(CancellationToken.None);
			await Task.Delay(100);

			await raiseEmailAsync(3, 7);

			await allProcessedAsync(1);
			(await rowsAsync()).ShouldHaveSingleItem().Channel.ShouldBe("email");
			mPush.Calls.ShouldBe(0);
		}

		[Fact]
		public async Task AMobileDataNotification_StoresNoTitleOrBody_AndIsOnlyPushed()
		{
			mPush.Destinations.Add(new FcmResolvedDestination(7, "tok-a", FcmPlatforms.Android));
			mMail.Destinations.Add(new EmailResolvedDestination(7, "sam@example.com"));
			await mService.StartAsync(CancellationToken.None);
			await Task.Delay(100);

			await raiseAsync((sender, repository) => sender.CreateMobileDataNotification(repository, 3, 1, """{ "k": "v" }""", [7]));

			await allProcessedAsync(1);
			var notification = (await notificationsAsync()).Single();
			notification.Title.ShouldBeNull();
			notification.Body.ShouldBeNull();
			notification.Payload.ShouldBe("""{ "k": "v" }""");
			notification.TransportType.ShouldBe(NotificationTransportType.FCM);
			(await rowsAsync()).ShouldHaveSingleItem().Channel.ShouldBe("fcm");
			mMail.Calls.ShouldBe(0);
		}

		[Fact]
		public async Task EveryRelayIsWokenOnceTheRowsAreCommitted()
		{
			mPush.Destinations.Add(new FcmResolvedDestination(7, "tok-a", FcmPlatforms.Android));
			mMail.Destinations.Add(new EmailResolvedDestination(7, "sam@example.com"));
			var signals = mProvider.GetRequiredService<OutboxSignals>();
			// The relays create their own signals when the host builds them; with none running here, do it up front.
			signals.For(Channels.Fcm);
			signals.For(Channels.Email);
			await mService.StartAsync(CancellationToken.None);
			await Task.Delay(100);

			await raiseMobileAsync(3, 7);

			foreach (var channel in new[] { Channels.Fcm, Channels.Email })
			{
				var waited = System.Diagnostics.Stopwatch.StartNew();
				await signals.For(channel).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
				waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), $"the {channel} relay should have been woken, not left to its poll");
			}
		}

		[Fact]
		public async Task AnEmailNotificationForAUserWithNoAddress_IsRecordedAsNotEmailed_AndNothingIsPushed()
		{
			mPush.Destinations.Add(new FcmResolvedDestination(7, "tok-a", FcmPlatforms.Android));
			await mService.StartAsync(CancellationToken.None);
			await Task.Delay(100);

			await raiseEmailAsync(3, 7);

			await allProcessedAsync(1);
			var mail = (await rowsAsync()).ShouldHaveSingleItem();
			mail.Channel.ShouldBe("email");
			mail.State.ShouldBe(OutboxState.Completed);
			mail.ErrorCode.ShouldBe(OutboxErrorCode.NoEmailAddress);
		}

		[Fact]
		public async Task TheSweep_FindsANewEmailNotification_AndMailsItOnly()
		{
			mPush.Destinations.Add(new FcmResolvedDestination(7, "tok-a", FcmPlatforms.Android));
			mMail.Destinations.Add(new EmailResolvedDestination(7, "sam@example.com"));
			var id = await addUnprocessedAsync(NotificationTransportType.Email, 7);

			await mService.processNotificationsTable(CancellationToken.None);

			var mail = (await rowsAsync()).ShouldHaveSingleItem();
			mail.Channel.ShouldBe("email");
			mail.NotificationId.ShouldBe(id);
			mPush.Calls.ShouldBe(0);
			(await notificationsAsync()).Single().Status.ShouldBe(NotificationStatus.Processed);
		}

		[Fact]
		public async Task TheSweep_FindsANewDefaultNotification_AndPushesItOnly()
		{
			mPush.Destinations.Add(new FcmResolvedDestination(7, "tok-a", FcmPlatforms.Android));
			mMail.Destinations.Add(new EmailResolvedDestination(7, "sam@example.com"));
			var id = await addUnprocessedAsync(transportType: null, 7); // TransportType never set: the default

			await mService.processNotificationsTable(CancellationToken.None);

			var push = (await rowsAsync()).ShouldHaveSingleItem();
			push.Channel.ShouldBe("fcm");
			push.NotificationId.ShouldBe(id);
			mMail.Calls.ShouldBe(0);
			(await notificationsAsync()).Single().Status.ShouldBe(NotificationStatus.Processed);
		}

		[Fact]
		public async Task AnEmailResolverThatFailsAtFirst_DoesNotMailTwice_WhenTheSweepRetries()
		{
			mMail.Destinations.Add(new EmailResolvedDestination(7, "sam@example.com"));
			mMail.Throws = new InvalidOperationException("address book down");
			await addUnprocessedAsync(NotificationTransportType.Email, 7);

			await mService.processNotificationsTable(CancellationToken.None); // the resolver fails; the notification stays New
			(await notificationsAsync()).Single().Status.ShouldBe(NotificationStatus.New);

			mMail.Throws = null; // the address book is back
			await mService.processNotificationsTable(CancellationToken.None);

			(await rowsAsync()).ShouldHaveSingleItem().Channel.ShouldBe("email");
			(await notificationsAsync()).Single().Status.ShouldBe(NotificationStatus.Processed);
		}
	}

	/// <summary>
	/// A host with the email transport off (the default). Nothing registered there can send an email, so an email
	/// notification must not sit at New until it expires: it is given up on at once, and the log says why.
	/// </summary>
	public sealed class EmailTransportOffTests : IAsyncDisposable
	{
		private readonly HostTestDatabase mDatabase = HostTestDatabase.Create();
		private readonly ListLogger<NotificationService> mLog = new();
		private readonly ServiceProvider mProvider;
		private readonly NotificationService mService;

		public EmailTransportOffTests()
		{
			var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
			{
				["NotificationService:SweepIntervalSeconds"] = "3600",
				["NotificationService:SweepGracePeriodSeconds"] = "0",
			}).Build();

			var services = new ServiceCollection();
			mDatabase.Register(services);
			services.AddNotificationSender(configuration); // push through the legacy queue, no email
			services.AddSingleton<ILogger<NotificationService>>(mLog);
			services.RemoveAll<IHostedService>();
			mProvider = services.BuildServiceProvider();
			mService = mProvider.GetRequiredService<NotificationService>();
		}

		public async ValueTask DisposeAsync()
		{
			await mService.StopAsync(CancellationToken.None);
			await mProvider.DisposeAsync();
			await mDatabase.DisposeAsync();
		}

		private async Task raiseAsync(Action<INotificationSender, IRepository> create)
		{
			using var scope = mProvider.CreateScope();
			var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
			create(scope.ServiceProvider.GetRequiredService<INotificationSender>(), repository);
			await repository.SaveAsync();
		}

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
		public async Task AnEmailNotification_IsExpiredAtOnce_WithAWarningNamingItAndItsTransport()
		{
			await mService.StartAsync(CancellationToken.None);
			await Task.Delay(100);

			await raiseAsync((sender, repository) => sender.CreateEmailNotification(repository, 3, 1, "Title", "Body", "{}", [7]));

			await eventuallyAsync(async () => (await mDatabase.QueryAsync(db => db.Set<Notification>().SingleAsync())).Status == NotificationStatus.Expired, "the notification to be given up on");
			var notification = await mDatabase.QueryAsync(db => db.Set<Notification>().SingleAsync());
			var warning = mLog.Entries.Single(e => e.Level == LogLevel.Warning && e.Message.Contains("no processor for it"));
			warning.Message.ShouldContain($"Notification {notification.Id} ");
			warning.Message.ShouldContain("Email");
			(await mDatabase.QueryAsync(db => db.Set<FcmQueue>().CountAsync())).ShouldBe(0); // and it was not pushed instead
			(await mDatabase.QueryAsync(db => db.Set<OutboxMessage>().CountAsync())).ShouldBe(0);
		}

		[Fact]
		public async Task AMobileNotification_IsStillPushed_OnThatHost()
		{
			await mService.StartAsync(CancellationToken.None);
			await Task.Delay(100);

			await raiseAsync((sender, repository) => sender.CreateMobileNotification(repository, 3, 1, "Title", "Body", "{}", [7]));

			await eventuallyAsync(async () => (await mDatabase.QueryAsync(db => db.Set<Notification>().SingleAsync())).Status == NotificationStatus.Processed, "the notification to be processed");
			(await mDatabase.QueryAsync(db => db.Set<FcmQueue>().SingleAsync())).UserId.ShouldBe(7);
			mLog.Entries.ShouldNotContain(e => e.Level == LogLevel.Warning);
		}
	}

	/// <summary>
	/// What happens to a notification when one processor fails after another has already added its rows, with
	/// processors that are not idempotent (the legacy queue one is not).
	/// </summary>
	public sealed class PartialFailureTests : IAsyncDisposable
	{
		private readonly HostTestDatabase mDatabase = HostTestDatabase.Create();

		public ValueTask DisposeAsync() => mDatabase.DisposeAsync();

		private sealed class QueueingProcessor : ITransportNotificationProcessor
		{
			public byte TransportType => NotificationTransportType.FCM;

			public Task ProcessNotificationAsync(IRepository repository, int notificationId, List<int> usersIds, CancellationToken cancellationToken)
			{
				foreach (var user in usersIds) repository.Create(new FcmQueue { Date = DateTime.UtcNow, NotificationId = notificationId, UserId = user });
				return Task.CompletedTask;
			}
		}

		private sealed class FailingFor : ITransportNotificationProcessor
		{
			private readonly int mNotificationId;

			public FailingFor(int notificationId) => mNotificationId = notificationId;

			// The same transport as QueueingProcessor: a notification only reaches the processors of its own transport, so for
			// one processor to fail after another has added rows for the same notification, the two have to share one.
			public byte TransportType => NotificationTransportType.FCM;

			public Task ProcessNotificationAsync(IRepository repository, int notificationId, List<int> usersIds, CancellationToken cancellationToken) =>
				notificationId == mNotificationId ? throw new InvalidOperationException("second processor failed") : Task.CompletedTask;
		}

		private async Task<int> addNotificationFor(int userId)
		{
			var notification = await mDatabase.AddNotificationAsync();
			await using var context = mDatabase.CreateContext();
			context.Set<NotificationToUser>().Add(new NotificationToUser { NotificationId = notification.Id, UserId = userId });
			await context.SaveChangesAsync();
			return notification.Id;
		}

		[Fact]
		public async Task WhenALaterProcessorFails_TheEarlierOnesWorkIsNotCommittedWithTheNextNotification()
		{
			var services = new ServiceCollection();
			mDatabase.Register(services);
			using var provider = services.BuildServiceProvider();
			var first = await addNotificationFor(1);  // its second processor will fail
			var second = await addNotificationFor(2); // fine
			var service = new NotificationService(
				provider.GetRequiredService<IServiceScopeFactory>(),
				[new QueueingProcessor(), new FailingFor(first)],
				Options.Create(new NotificationServiceOptions { SweepGracePeriodSeconds = 0 }),
				NullLogger<NotificationService>.Instance);
			await Task.Delay(50);

			await service.processNotificationsTable(CancellationToken.None);

			var queued = await mDatabase.QueryAsync(db => db.Set<FcmQueue>().AsNoTracking().ToListAsync());
			// The first notification stays New for the sweep to retry, so nothing of it may be queued yet; if its
			// half-done work were saved along with the second notification's, the retry would queue it again.
			queued.ShouldHaveSingleItem().NotificationId.ShouldBe(second);
			var statuses = await mDatabase.QueryAsync(db => db.Set<Notification>().AsNoTracking().ToDictionaryAsync(n => n.Id, n => n.Status));
			statuses[first].ShouldBe(NotificationStatus.New);
			statuses[second].ShouldBe(NotificationStatus.Processed);
		}
	}
}
