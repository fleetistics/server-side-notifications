using exs.Database.Commons.Impl;
using exs.notifications_contracts;
using exs.notifications_database;
using exs.notifications_model.Outbox;
using exs.notifications_service.Interfaces;
using exs.notifications_service.Sender;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.notifications_service.Tests.Sender
{
	/// <summary>
	/// Two channels share the outbox table, each with its own relay and puller. These pin that they stay out of
	/// each other's way: a relay submits only its own channel's rows to its own sender, a puller keeps its own
	/// cursor, and what "dead destination" means is decided per channel.
	/// </summary>
	public sealed class EmailChannelRelayAndPullerTests : IAsyncDisposable
	{
		private readonly HostTestDatabase mDatabase = HostTestDatabase.Create();
		private readonly FakeNotificationTransport mTransport = new();
		private readonly ManualTimeProvider mClock = new();
		private readonly ListLogger<ResultPuller> mPullerLog = new();
		private readonly List<IAsyncDisposable> mDisposables = new();
		private ServiceProvider? mProvider;

		public async ValueTask DisposeAsync()
		{
			foreach (var disposable in mDisposables) await disposable.DisposeAsync();
			if (mProvider != null) await mProvider.DisposeAsync();
			await mDatabase.DisposeAsync();
		}

		private IServiceScopeFactory scopes()
		{
			if (mProvider == null)
			{
				var services = new ServiceCollection();
				mDatabase.Register(services);
				mProvider = services.BuildServiceProvider();
			}
			return mProvider.GetRequiredService<IServiceScopeFactory>();
		}

		private static SenderTransportOptions options() => new() { BaseUrl = "http://sender.test", ApiKey = "k", RelayPollIntervalSeconds = 60, ResultPageSize = 100 };

		private OutboxRelay relay(string channel, int batchSize = 100)
		{
			var o = options();
			o.RelayBatchSize = batchSize;
			return new(scopes(), mTransport, Options.Create(o), new OutboxSignal(), mClock, NullLogger<OutboxRelay>.Instance, channel);
		}

		private ResultPuller puller(string channel, IServiceProvider? hooks = null) =>
			new(scopes(), mTransport, hooks ?? new ServiceCollection().BuildServiceProvider(), Options.Create(options()), mClock, mPullerLog, channel);

		private async Task<OutboxMessage> addRowAsync(string channel, byte state = OutboxState.Pending, int userId = 1, string? @ref = "r:1", DateTime? completedAt = null, TimeSpan? age = null)
		{
			var now = mClock.GetUtcNow().UtcDateTime;
			var notification = await mDatabase.AddNotificationAsync();
			await using var context = mDatabase.CreateContext();
			var row = new OutboxMessage
			{
				MessageId = Guid.NewGuid(),
				NotificationId = notification.Id,
				UserId = userId,
				Channel = channel,
				Ref = @ref,
				Envelope = state == OutboxState.Pending ? $$"""{ "channel": "{{channel}}" }""" : null,
				CreatedAt = now - (age ?? TimeSpan.Zero),
				ExpiresAt = now.AddMinutes(30),
				State = state,
				CompletedAt = completedAt,
			};
			context.Set<OutboxMessage>().Add(row);
			await context.SaveChangesAsync();
			return row;
		}

		private Task<OutboxMessage> rowAsync(long id) => mDatabase.QueryAsync(db => db.Set<OutboxMessage>().AsNoTracking().SingleAsync(o => o.Id == id));

		private ResultEvent result(Guid messageId, long resultId, string channel, bool destinationInvalid = false, string destination = "sam@example.com") => new()
		{
			SchemaVersion = ContractVersion.Current,
			ResultId = resultId,
			MessageId = messageId,
			Channel = channel,
			Status = destinationInvalid ? ResultStatuses.Failed : ResultStatuses.Sent,
			Destination = destination,
			DestinationInvalid = destinationInvalid,
			ErrorKind = destinationInvalid ? ErrorKinds.DestinationInvalid : null,
			ErrorCode = destinationInvalid ? "550 5.1.1" : null,
			Attempts = 1,
			CompletedAt = new DateTimeOffset(2026, 10, 17, 12, 0, 5, TimeSpan.Zero),
		};

		private static ResultPage page(long nextAfter, params ResultEvent[] results) => new() { Results = results.ToList(), NextAfter = nextAfter };

		private Task<long?> cursorAsync(string name) => mDatabase.QueryAsync(async db =>
			(long?)(await db.Set<TransportCursor>().AsNoTracking().SingleOrDefaultAsync(c => c.Name == name))?.Position);

		// ---- the relay ------------------------------------------------------------------------------

		[Fact]
		public async Task AnEmailRelay_SubmitsOnlyEmailRows()
		{
			var push = await addRowAsync("fcm");
			var mail = await addRowAsync("email");
			var emailRelay = relay("email");

			(await emailRelay.SubmitOneAsync(mail.Id, CancellationToken.None)).ShouldBe(RelayOutcome.Submitted);
			(await emailRelay.SubmitOneAsync(push.Id, CancellationToken.None)).ShouldBe(RelayOutcome.Skipped);

			mTransport.Submitted.ShouldHaveSingleItem().ShouldContain("email");
			(await rowAsync(push.Id)).State.ShouldBe(OutboxState.Pending);
			(await rowAsync(mail.Id)).State.ShouldBe(OutboxState.Submitted);
		}

		[Fact]
		public async Task APushRelay_LeavesEmailRowsAlone()
		{
			var mail = await addRowAsync("email");

			(await relay("fcm").SubmitOneAsync(mail.Id, CancellationToken.None)).ShouldBe(RelayOutcome.Skipped);

			mTransport.Submitted.ShouldBeEmpty();
			(await rowAsync(mail.Id)).State.ShouldBe(OutboxState.Pending);
		}

		[Fact]
		public async Task TheRunningEmailRelay_PicksUpItsPendingRowsOnly()
		{
			var push = await addRowAsync("fcm");
			var mail = await addRowAsync("email");
			var emailRelay = relay("email");
			mDisposables.Add(new Stopper(emailRelay));

			await emailRelay.StartAsync(CancellationToken.None);
			await eventuallyAsync(async () => (await rowAsync(mail.Id)).State == OutboxState.Submitted, "the email row to be submitted");
			await Task.Delay(150);

			(await rowAsync(push.Id)).State.ShouldBe(OutboxState.Pending);
			mTransport.Submitted.Count.ShouldBe(1);
		}

		[Fact]
		public async Task AnEmailRelay_IsNotCrowdedOutOfItsBatchByThePushBacklogAheadOfIt()
		{
			// A dozen older push rows keep a batch of two full if the poll does not look at the channel, and the
			// email row behind them would then never be handed to a worker.
			for (var i = 0; i < 12; i++) await addRowAsync("fcm", age: TimeSpan.FromMinutes(60 - i));
			var mail = await addRowAsync("email"); // the newest, so the poll takes the push rows first
			var emailRelay = relay("email", batchSize: 2);
			mDisposables.Add(new Stopper(emailRelay));

			await emailRelay.StartAsync(CancellationToken.None);

			await eventuallyAsync(async () => (await rowAsync(mail.Id)).State == OutboxState.Submitted, "the email row to be submitted");
			mTransport.Submitted.Count.ShouldBe(1); // and nothing of the push backlog went to the email sender
		}

		[Fact]
		public async Task PruningOneChannel_NeverTouchesTheOthersFinishedRows()
		{
			var old = mClock.GetUtcNow().UtcDateTime.AddDays(-90);
			var oldPush = await addRowAsync("fcm", OutboxState.Completed, completedAt: old);
			var oldMail = await addRowAsync("email", OutboxState.Completed, completedAt: old);

			(await relay("email").PruneOnceAsync(CancellationToken.None)).ShouldBe(1);

			await Should.ThrowAsync<InvalidOperationException>(() => rowAsync(oldMail.Id));
			(await rowAsync(oldPush.Id)).State.ShouldBe(OutboxState.Completed);
		}

		// ---- the puller -----------------------------------------------------------------------------

		[Fact]
		public async Task EachChannelsPuller_KeepsItsOwnCursor()
		{
			var mail = await addRowAsync("email", OutboxState.Submitted);
			var push = await addRowAsync("fcm", OutboxState.Submitted);

			mTransport.OnFetch = (_, _) => page(4, result(mail.MessageId, 4, "email"));
			await puller("email").PullOnceAsync(CancellationToken.None);
			mTransport.OnFetch = (_, _) => page(9, result(push.MessageId, 9, "fcm", destination: "tok"));
			await puller("fcm", new ServiceCollection().AddSingleton<IFcmTokenInvalidator, FakeFcmTokenInvalidator>().BuildServiceProvider()).PullOnceAsync(CancellationToken.None);

			(await cursorAsync("email-sender")).ShouldBe(4);
			(await cursorAsync("fcm-sender")).ShouldBe(9);
			ResultPuller.CursorNameFor(Channels.Fcm).ShouldBe(ResultPuller.CursorName); // the push cursor kept its name
		}

		[Fact]
		public async Task TheEmailPuller_ReadsFromItsOwnCursor_NotThePushOne()
		{
			await using (var context = mDatabase.CreateContext())
			{
				context.Set<TransportCursor>().Add(new TransportCursor { Name = "fcm-sender", Position = 500, UpdatedAt = DateTime.UtcNow });
				await context.SaveChangesAsync();
			}

			await puller("email").PullOnceAsync(CancellationToken.None);

			mTransport.FetchPositions.ShouldBe([0L]);
		}

		[Fact]
		public async Task ADeadEmailAddress_IsHandedToTheHostsInvalidator_WithTheRefAndTheAddress()
		{
			var row = await addRowAsync("email", OutboxState.Submitted, userId: 7, @ref: "u:7");
			var invalidator = new FakeEmailAddressInvalidator();
			var hooks = new ServiceCollection().AddSingleton<IEmailAddressInvalidator>(invalidator).BuildServiceProvider();
			mTransport.OnFetch = (_, _) => page(1, result(row.MessageId, 1, "email", destinationInvalid: true, destination: "ghost@example.com"));

			await puller("email", hooks).PullOnceAsync(CancellationToken.None);

			var done = await rowAsync(row.Id);
			done.State.ShouldBe(OutboxState.Completed);
			done.DestinationInvalid.ShouldBeTrue();
			done.ErrorCode.ShouldBe("550 5.1.1");
			invalidator.Calls.ShouldHaveSingleItem().ShouldBe((7, "u:7", "ghost@example.com"));
		}

		[Fact]
		public async Task WithoutAnInvalidator_ADeadEmailAddressIsStillRecorded_AndNothingFails_AndTheAddressIsNotLogged()
		{
			var row = await addRowAsync("email", OutboxState.Submitted, userId: 7);
			mTransport.OnFetch = (_, _) => page(1, result(row.MessageId, 1, "email", destinationInvalid: true, destination: "ghost@example.com"));

			await puller("email").PullOnceAsync(CancellationToken.None); // no hooks at all: must not throw

			(await rowAsync(row.Id)).DestinationInvalid.ShouldBeTrue();
			(await cursorAsync("email-sender")).ShouldBe(1);
			mPullerLog.Entries.ShouldNotBeEmpty();
			mPullerLog.Entries.ShouldAllBe(e => !e.Message.Contains("ghost@example.com"));
		}

		// ---- free-form emails: rows with no notification and no user ------------------------------------------

		/// <summary>A row exactly as INotificationSender.CreateEmail writes it, through the real writer.</summary>
		private async Task<OutboxMessage> addFreeFormRowAsync(string to = "reset@example.com")
		{
			var options = new ServiceCollection().Configure<SenderTransportOptions>("email", o => { o.BaseUrl = "http://mail.test"; o.ApiKey = "k"; }).BuildServiceProvider()
				.GetRequiredService<IOptionsMonitor<SenderTransportOptions>>();
			var writer = new EmailOutboxWriter(options, mClock);
			await using var context = mDatabase.CreateContext();
			var row = writer.Write(new EntityFrameworkRepository<NotificationDatabaseContext>(context), new EmailMessage { To = to, Subject = "Reset", Text = "https://example.com/reset?token=abc" });
			await context.SaveChangesAsync();
			return row;
		}

		/// <summary>As the relay leaves a row once the sender has accepted it.</summary>
		private async Task markSubmittedAsync(long id)
		{
			await using var context = mDatabase.CreateContext();
			var tracked = await context.Set<OutboxMessage>().SingleAsync(o => o.Id == id);
			tracked.State = OutboxState.Submitted;
			tracked.Envelope = null;
			await context.SaveChangesAsync();
		}

		[Fact]
		public async Task AFreeFormEmailRow_IsSubmittedLikeAnyOther_AndItsEnvelopeIsClearedOnceTheSenderAcceptsIt()
		{
			var row = await addFreeFormRowAsync();
			(await rowAsync(row.Id)).Envelope.ShouldNotBeNullOrEmpty();

			(await relay("email").SubmitOneAsync(row.Id, CancellationToken.None)).ShouldBe(RelayOutcome.Submitted);

			var done = await rowAsync(row.Id);
			done.State.ShouldBe(OutboxState.Submitted);
			done.SubmittedAt.ShouldNotBeNull();
			done.Envelope.ShouldBeNull(); // the address and the link are not kept once the sender has them
			done.NotificationId.ShouldBeNull();
			done.UserId.ShouldBeNull();
			ContractJson.Deserialize<MessageEnvelope<EmailBody>>(mTransport.Submitted.Single())!.Body.To.Address.ShouldBe("reset@example.com");
		}

		[Fact]
		public async Task TheRunningEmailRelay_PicksUpAFreeFormRowToo()
		{
			var row = await addFreeFormRowAsync();
			var emailRelay = relay("email");
			mDisposables.Add(new Stopper(emailRelay));

			await emailRelay.StartAsync(CancellationToken.None);

			await eventuallyAsync(async () => (await rowAsync(row.Id)).State == OutboxState.Submitted, "the free-form row to be submitted");
		}

		[Fact]
		public async Task ADeadAddressOnAFreeFormRow_IsRecorded_WithoutCallingTheInvalidator_AndWithoutThrowing()
		{
			var row = await addFreeFormRowAsync(to: "ghost@example.com");
			await markSubmittedAsync(row.Id);
			var invalidator = new FakeEmailAddressInvalidator();
			var hooks = new ServiceCollection().AddSingleton<IEmailAddressInvalidator>(invalidator).BuildServiceProvider();
			mTransport.OnFetch = (_, _) => page(1, result(row.MessageId, 1, "email", destinationInvalid: true, destination: "ghost@example.com"));

			await Should.NotThrowAsync(() => puller("email", hooks).PullOnceAsync(CancellationToken.None));

			var done = await rowAsync(row.Id);
			done.State.ShouldBe(OutboxState.Completed);
			done.DestinationInvalid.ShouldBeTrue();
			done.ErrorCode.ShouldBe("550 5.1.1");
			invalidator.Calls.ShouldBeEmpty(); // it is keyed by user, and this mail has none
			(await cursorAsync("email-sender")).ShouldBe(1);
			mPullerLog.Entries.ShouldAllBe(e => !e.Message.Contains("ghost@example.com"));
		}

		[Fact]
		public async Task AFreeFormRowWithAnOrdinaryResult_IsCompletedLikeAnyOther()
		{
			var row = await addFreeFormRowAsync();
			await markSubmittedAsync(row.Id);
			mTransport.OnFetch = (_, _) => page(1, result(row.MessageId, 1, "email"));

			await puller("email").PullOnceAsync(CancellationToken.None);

			var done = await rowAsync(row.Id);
			done.State.ShouldBe(OutboxState.Completed);
			done.ResultStatus.ShouldBe(ResultStatuses.Sent);
		}

		[Fact]
		public async Task AnInvalidatorThatThrows_DoesNotUndoTheRecordedResult()
		{
			var row = await addRowAsync("email", OutboxState.Submitted);
			var invalidator = new FakeEmailAddressInvalidator { Throws = new InvalidOperationException("db down") };
			var hooks = new ServiceCollection().AddSingleton<IEmailAddressInvalidator>(invalidator).BuildServiceProvider();
			mTransport.OnFetch = (_, _) => page(1, result(row.MessageId, 1, "email", destinationInvalid: true));

			await puller("email", hooks).PullOnceAsync(CancellationToken.None);

			(await rowAsync(row.Id)).State.ShouldBe(OutboxState.Completed);
			(await cursorAsync("email-sender")).ShouldBe(1);
		}

		[Fact]
		public async Task ADeadPushToken_StillNeedsThePushHook_WhateverTheEmailSideHas()
		{
			var row = await addRowAsync("fcm", OutboxState.Submitted);
			mTransport.OnFetch = (_, _) => page(1, result(row.MessageId, 1, "fcm", destinationInvalid: true, destination: "tok"));
			var onlyEmailHook = new ServiceCollection().AddSingleton<IEmailAddressInvalidator, FakeEmailAddressInvalidator>().BuildServiceProvider();

			await puller("fcm", onlyEmailHook).PullOnceAsync(CancellationToken.None);

			(await rowAsync(row.Id)).State.ShouldBe(OutboxState.Completed); // the record is committed first
			mPullerLog.Entries.ShouldContain(e => e.Message.Contains("dead fcm destination"));
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

		private sealed class Stopper : IAsyncDisposable
		{
			private readonly OutboxRelay mRelay;

			public Stopper(OutboxRelay relay) => mRelay = relay;

			public async ValueTask DisposeAsync() => await mRelay.StopAsync(CancellationToken.None);
		}
	}
}
