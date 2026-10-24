using exs.notifications_contracts;
using exs.notifications_model.Outbox;
using exs.notifications_service;
using exs.notifications_service.Sender;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.notifications_service.Tests.Sender
{
	public sealed class ResultPullerTests : IAsyncDisposable
	{
		private readonly HostTestDatabase mDatabase = HostTestDatabase.Create();
		private readonly FakeNotificationTransport mTransport = new();
		private readonly FakeFcmTokenInvalidator mInvalidator = new();
		private readonly ManualTimeProvider mClock = new();
		private ServiceProvider? mProvider;
		private ResultPuller? mPuller;

		public async ValueTask DisposeAsync()
		{
			if (mPuller != null) await mPuller.StopAsync(CancellationToken.None);
			if (mProvider != null) await mProvider.DisposeAsync();
			await mDatabase.DisposeAsync();
		}

		private ResultPuller puller(Action<SenderTransportOptions>? configure = null)
		{
			var options = new SenderTransportOptions { BaseUrl = "http://sender.test", ApiKey = "k", ResultPageSize = 100, ResultPollIntervalSeconds = 1 };
			configure?.Invoke(options);
			var services = new ServiceCollection();
			mDatabase.Register(services);
			mProvider = services.BuildServiceProvider();
			var hooks = new ServiceCollection().AddSingleton<IFcmTokenInvalidator>(mInvalidator).BuildServiceProvider();
			return mPuller = new ResultPuller(mProvider.GetRequiredService<IServiceScopeFactory>(), mTransport, hooks, Options.Create(options), mClock, NullLogger<ResultPuller>.Instance);
		}

		private async Task<OutboxMessage> addRowAsync(byte state = OutboxState.Submitted, int userId = 1, string? @ref = "s:10")
		{
			var notification = await mDatabase.AddNotificationAsync();
			await using var context = mDatabase.CreateContext();
			var row = new OutboxMessage
			{
				MessageId = Guid.NewGuid(),
				NotificationId = notification.Id,
				UserId = userId,
				Channel = "fcm",
				Ref = @ref,
				Envelope = state == OutboxState.Pending ? "{}" : null,
				CreatedAt = mClock.GetUtcNow().UtcDateTime,
				ExpiresAt = mClock.GetUtcNow().UtcDateTime.AddMinutes(30),
				State = state,
			};
			context.Set<OutboxMessage>().Add(row);
			await context.SaveChangesAsync();
			return row;
		}

		private ResultEvent result(Guid messageId, long resultId, string status = ResultStatuses.Sent, string? errorKind = null, string? errorCode = null, bool destinationInvalid = false, string? destination = "tok", string? providerId = "projects/p/messages/1", int attempts = 1) => new()
		{
			SchemaVersion = ContractVersion.Current,
			ResultId = resultId,
			MessageId = messageId,
			Channel = "fcm",
			Status = status,
			Destination = destination,
			DestinationInvalid = destinationInvalid,
			ErrorKind = errorKind,
			ErrorCode = errorCode,
			ProviderMessageId = providerId,
			Attempts = attempts,
			CompletedAt = new DateTimeOffset(2026, 10, 17, 12, 0, 5, TimeSpan.Zero),
		};

		private static ResultPage page(long nextAfter, params ResultEvent[] results) => new() { Results = results.ToList(), NextAfter = nextAfter };

		private Task<OutboxMessage> rowAsync(long id) => mDatabase.QueryAsync(db => db.Set<OutboxMessage>().AsNoTracking().SingleAsync(o => o.Id == id));

		private Task<long?> cursorAsync() => mDatabase.QueryAsync(async db =>
			(long?)(await db.Set<TransportCursor>().AsNoTracking().SingleOrDefaultAsync(c => c.Name == ResultPuller.CursorName))?.Position);

		[Fact]
		public async Task AResultIsAppliedToItsRow_WhichBecomesTheDeliveryRecord()
		{
			var row = await addRowAsync();
			mTransport.OnFetch = (_, _) => page(7, result(row.MessageId, 7, providerId: "projects/p/messages/abc", attempts: 2));

			await puller().PullOnceAsync(CancellationToken.None);

			var done = await rowAsync(row.Id);
			done.State.ShouldBe(OutboxState.Completed);
			done.ResultId.ShouldBe(7);
			done.ResultStatus.ShouldBe(ResultStatuses.Sent);
			done.ProviderMessageId.ShouldBe("projects/p/messages/abc");
			done.Attempts.ShouldBe(2);
			done.DestinationInvalid.ShouldBeFalse();
			done.ErrorKind.ShouldBeNull();
			done.CompletedAt.ShouldBe(new DateTime(2026, 10, 17, 12, 0, 5, DateTimeKind.Utc));
			done.Envelope.ShouldBeNull();
		}

		[Fact]
		public async Task AFailedResult_KeepsItsErrorDetails()
		{
			var row = await addRowAsync();
			mTransport.OnFetch = (_, _) => page(1, result(row.MessageId, 1, ResultStatuses.Failed, ErrorKinds.Permanent, "SenderIdMismatch", providerId: null));

			await puller().PullOnceAsync(CancellationToken.None);

			var done = await rowAsync(row.Id);
			done.State.ShouldBe(OutboxState.Completed); // the sender's job is finished; ResultStatus says how it went
			done.ResultStatus.ShouldBe(ResultStatuses.Failed);
			done.ErrorKind.ShouldBe(ErrorKinds.Permanent);
			done.ErrorCode.ShouldBe("SenderIdMismatch");
			done.ProviderMessageId.ShouldBeNull();
		}

		[Fact]
		public async Task TheCursorIsPersisted_AndTheNextPullStartsFromIt()
		{
			var row = await addRowAsync();
			mTransport.OnFetch = (after, _) => after == 0 ? page(42, result(row.MessageId, 42)) : page(after);
			var subject = puller();

			await subject.PullOnceAsync(CancellationToken.None);
			await subject.PullOnceAsync(CancellationToken.None);

			(await cursorAsync()).ShouldBe(42);
			mTransport.FetchPositions.ShouldBe([0L, 42L]);
		}

		[Fact]
		public async Task ARestartedPuller_ResumesFromTheStoredCursor_NotFromTheStart()
		{
			var row = await addRowAsync();
			mTransport.OnFetch = (after, _) => after == 0 ? page(5, result(row.MessageId, 5)) : page(after);
			await puller().PullOnceAsync(CancellationToken.None);

			await puller().PullOnceAsync(CancellationToken.None); // a brand new instance, as after a restart

			mTransport.FetchPositions.ShouldBe([0L, 5L]);
		}

		[Fact]
		public async Task AnEmptyPage_ChangesNothing_AndSaysThereIsNoMore()
		{
			mTransport.OnFetch = (after, _) => page(after);

			(await puller().PullOnceAsync(CancellationToken.None)).ShouldBeFalse();

			(await cursorAsync()).ShouldBeNull();
		}

		[Fact]
		public async Task AFullPage_SaysThereIsMoreWaiting()
		{
			var rows = new[] { await addRowAsync(), await addRowAsync() };
			mTransport.OnFetch = (_, _) => page(2, result(rows[0].MessageId, 1), result(rows[1].MessageId, 2));

			(await puller(o => o.ResultPageSize = 2).PullOnceAsync(CancellationToken.None)).ShouldBeTrue();
		}

		[Fact]
		public async Task APartialPage_SaysThereIsNoMore()
		{
			var row = await addRowAsync();
			mTransport.OnFetch = (_, _) => page(1, result(row.MessageId, 1));

			(await puller(o => o.ResultPageSize = 2).PullOnceAsync(CancellationToken.None)).ShouldBeFalse();
		}

		[Fact]
		public async Task AResultForAMessageThisHostHasNoRowFor_IsSkipped_ButTheCursorStillMovesOn()
		{
			var row = await addRowAsync();
			mTransport.OnFetch = (_, _) => page(9, result(Guid.NewGuid(), 8), result(row.MessageId, 9));

			await puller().PullOnceAsync(CancellationToken.None);

			(await rowAsync(row.Id)).State.ShouldBe(OutboxState.Completed);
			(await cursorAsync()).ShouldBe(9);
		}

		[Fact]
		public async Task ARowThatIsAlreadyCompleted_IsNotOverwritten_WhenAPageIsAppliedTwice()
		{
			var row = await addRowAsync();
			mTransport.OnFetch = (_, _) => page(3, result(row.MessageId, 3, providerId: "first"));
			var subject = puller();
			await subject.PullOnceAsync(CancellationToken.None);

			mTransport.OnFetch = (_, _) => page(3, result(row.MessageId, 3, providerId: "second", destinationInvalid: true));
			await subject.PullOnceAsync(CancellationToken.None);

			var done = await rowAsync(row.Id);
			done.ProviderMessageId.ShouldBe("first");
			done.DestinationInvalid.ShouldBeFalse();
			mInvalidator.Calls.ShouldBeEmpty(); // and a replayed page does not clear a token a second time
		}

		[Theory]
		[InlineData(OutboxState.Pending)]
		[InlineData(OutboxState.Submitted)]
		public async Task AResultIsTheTruth_WhateverStateTheRowWasIn(byte state)
		{
			// Pending can happen if the host crashed after the sender accepted a message but before it saved that.
			var row = await addRowAsync(state);
			mTransport.OnFetch = (_, _) => page(1, result(row.MessageId, 1));

			await puller().PullOnceAsync(CancellationToken.None);

			var done = await rowAsync(row.Id);
			done.State.ShouldBe(OutboxState.Completed);
			done.Envelope.ShouldBeNull();
		}

		[Fact]
		public async Task ADeadDestination_IsHandedToTheInvalidator_WithTheUsersRefAndTheEchoedToken()
		{
			var row = await addRowAsync(userId: 7, @ref: "s:123");
			mTransport.OnFetch = (_, _) => page(1, result(row.MessageId, 1, ResultStatuses.Failed, ErrorKinds.DestinationInvalid, "Unregistered", destinationInvalid: true, destination: "the-dead-token"));

			await puller().PullOnceAsync(CancellationToken.None);

			mInvalidator.Calls.ShouldHaveSingleItem().ShouldBe((7, "s:123", "the-dead-token"));
			(await rowAsync(row.Id)).DestinationInvalid.ShouldBeTrue();
		}

		[Fact]
		public async Task OnlyDeadDestinationsReachTheInvalidator()
		{
			var fine = await addRowAsync();
			var failedButNotDead = await addRowAsync();
			var deadWithoutAToken = await addRowAsync();
			mTransport.OnFetch = (_, _) => page(3,
				result(fine.MessageId, 1),
				result(failedButNotDead.MessageId, 2, ResultStatuses.Failed, ErrorKinds.Permanent, "SenderIdMismatch"),
				result(deadWithoutAToken.MessageId, 3, ResultStatuses.Failed, ErrorKinds.DestinationInvalid, destinationInvalid: true, destination: null));

			await puller().PullOnceAsync(CancellationToken.None);

			mInvalidator.Calls.ShouldBeEmpty();
		}

		[Fact]
		public async Task TheRecordAndTheCursorAreSaved_EvenWhenClearingTheTokenFails()
		{
			// Housekeeping must not undo the delivery record: a failure here is logged, and the next push to
			// that token is simply reported dead again.
			var row = await addRowAsync();
			mInvalidator.Throws = new InvalidOperationException("the user_session update failed");
			mTransport.OnFetch = (_, _) => page(1, result(row.MessageId, 1, ResultStatuses.Failed, ErrorKinds.DestinationInvalid, destinationInvalid: true));

			await Should.NotThrowAsync(() => puller().PullOnceAsync(CancellationToken.None));

			(await rowAsync(row.Id)).State.ShouldBe(OutboxState.Completed);
			(await cursorAsync()).ShouldBe(1);
		}

		[Fact]
		public async Task WithoutAnInvalidatorRegistered_TheRecordIsStillSaved_AndTheProblemIsLoggedNotThrown()
		{
			var row = await addRowAsync();
			var services = new ServiceCollection();
			mDatabase.Register(services);
			mProvider = services.BuildServiceProvider();
			mTransport.OnFetch = (_, _) => page(1, result(row.MessageId, 1, ResultStatuses.Failed, ErrorKinds.DestinationInvalid, destinationInvalid: true));
			var noHooks = new ServiceCollection().BuildServiceProvider();
			var subject = new ResultPuller(mProvider.GetRequiredService<IServiceScopeFactory>(), mTransport, noHooks, Options.Create(new SenderTransportOptions { ResultPageSize = 100 }), mClock, NullLogger<ResultPuller>.Instance);

			await Should.NotThrowAsync(() => subject.PullOnceAsync(CancellationToken.None));

			(await rowAsync(row.Id)).State.ShouldBe(OutboxState.Completed);
			(await cursorAsync()).ShouldBe(1);
		}

		[Fact]
		public async Task OneFailingInvalidation_DoesNotStopTheOthersInThePage()
		{
			var first = await addRowAsync(userId: 1);
			var second = await addRowAsync(userId: 2);
			mInvalidator.Throws = new InvalidOperationException("boom");
			mTransport.OnFetch = (_, _) => page(2,
				result(first.MessageId, 1, ResultStatuses.Failed, destinationInvalid: true, destination: "a"),
				result(second.MessageId, 2, ResultStatuses.Failed, destinationInvalid: true, destination: "b"));

			await puller().PullOnceAsync(CancellationToken.None);

			mInvalidator.Calls.Select(c => c.Destination).ShouldBe(["a", "b"]);
		}

		[Fact]
		public async Task ASeveralResultPage_IsAppliedInOneGo()
		{
			var rows = new List<OutboxMessage>();
			for (var i = 0; i < 5; i++) rows.Add(await addRowAsync());
			mTransport.OnFetch = (_, _) => page(5, rows.Select((r, i) => result(r.MessageId, i + 1)).ToArray());

			await puller().PullOnceAsync(CancellationToken.None);

			(await mDatabase.QueryAsync(db => db.Set<OutboxMessage>().CountAsync(o => o.State == OutboxState.Completed))).ShouldBe(5);
		}

		[Fact]
		public async Task ASenderThatCannotBeRead_IsAnException_AndTheCursorStaysPut()
		{
			var row = await addRowAsync();
			mTransport.OnFetch = (_, _) => page(1, result(row.MessageId, 1));
			var subject = puller();
			await subject.PullOnceAsync(CancellationToken.None);
			mTransport.OnFetch = (_, _) => throw new HttpRequestException("the sender is down");

			await Should.ThrowAsync<HttpRequestException>(() => subject.PullOnceAsync(CancellationToken.None));

			(await cursorAsync()).ShouldBe(1);
		}

		[Fact]
		public async Task ACursorNeverMovesBackwards()
		{
			var row = await addRowAsync();
			mTransport.OnFetch = (_, _) => page(10, result(row.MessageId, 10));
			var subject = puller();
			await subject.PullOnceAsync(CancellationToken.None);

			mTransport.OnFetch = (_, _) => page(3, result(Guid.NewGuid(), 3)); // a misbehaving sender
			await subject.PullOnceAsync(CancellationToken.None);

			(await cursorAsync()).ShouldBe(10);
		}

		[Fact]
		public async Task TheLoop_BacksOffAfterAFailure_AndCarriesOnOnceTheSenderIsBack()
		{
			var row = await addRowAsync();
			var calls = 0;
			mTransport.OnFetch = (after, _) => ++calls == 1
				? throw new HttpRequestException("down")
				: after == 0 ? page(1, result(row.MessageId, 1)) : page(after);

			await puller().StartAsync(CancellationToken.None);

			var deadline = DateTime.UtcNow.AddSeconds(15);
			while (DateTime.UtcNow < deadline && (await rowAsync(row.Id)).State != OutboxState.Completed) await Task.Delay(50);
			(await rowAsync(row.Id)).State.ShouldBe(OutboxState.Completed);
			calls.ShouldBeGreaterThanOrEqualTo(2);
		}
	}
}
