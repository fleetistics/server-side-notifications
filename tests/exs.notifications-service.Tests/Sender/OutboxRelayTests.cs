using exs.notifications_model.Outbox;
using exs.notifications_service.Sender;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.notifications_service.Tests.Sender
{
	public sealed class OutboxRelayTests : IAsyncDisposable
	{
		private readonly HostTestDatabase mDatabase = HostTestDatabase.Create();
		private readonly FakeNotificationTransport mTransport = new();
		private readonly ManualTimeProvider mClock = new();
		private readonly OutboxSignal mSignal = new();
		private ServiceProvider? mProvider;
		private OutboxRelay? mRelay;

		public async ValueTask DisposeAsync()
		{
			if (mRelay != null) await mRelay.StopAsync(CancellationToken.None);
			if (mProvider != null) await mProvider.DisposeAsync();
			await mDatabase.DisposeAsync();
		}

		private OutboxRelay relay(Action<SenderTransportOptions>? configure = null)
		{
			var options = new SenderTransportOptions { BaseUrl = "http://sender.test", ApiKey = "k", RelayPollIntervalSeconds = 60, RelayMaxConcurrency = 4 };
			configure?.Invoke(options);
			var services = new ServiceCollection();
			mDatabase.Register(services);
			mProvider = services.BuildServiceProvider();
			return mRelay = new OutboxRelay(mProvider.GetRequiredService<IServiceScopeFactory>(), mTransport, Options.Create(options), mSignal, mClock, NullLogger<OutboxRelay>.Instance);
		}

		private async Task<long> addRowAsync(byte state = OutboxState.Pending, string? envelope = "{\"to\":\"x\"}", TimeSpan? expiresIn = null, DateTime? completedAt = null, string? tag = null)
		{
			var now = mClock.GetUtcNow().UtcDateTime;
			var notification = await mDatabase.AddNotificationAsync();
			await using var context = mDatabase.CreateContext();
			var row = new OutboxMessage
			{
				MessageId = Guid.NewGuid(),
				NotificationId = notification.Id,
				UserId = 1,
				Channel = "fcm",
				Ref = tag,
				Envelope = envelope,
				CreatedAt = now,
				ExpiresAt = now + (expiresIn ?? TimeSpan.FromMinutes(30)),
				State = state,
				CompletedAt = completedAt,
			};
			context.Set<OutboxMessage>().Add(row);
			await context.SaveChangesAsync();
			return row.Id;
		}

		private Task<OutboxMessage> rowAsync(long id) => mDatabase.QueryAsync(db => db.Set<OutboxMessage>().AsNoTracking().SingleAsync(o => o.Id == id));

		private Task<int> countInStateAsync(byte state) => mDatabase.QueryAsync(db => db.Set<OutboxMessage>().CountAsync(o => o.State == state));

		private static async Task eventuallyAsync(Func<Task<bool>> condition, string what, int timeoutMilliseconds = 10_000)
		{
			var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
			while (DateTime.UtcNow < deadline)
			{
				if (await condition()) return;
				await Task.Delay(25);
			}
			throw new TimeoutException($"Timed out waiting for: {what}");
		}

		public class SubmittingOneRow : IAsyncDisposable
		{
			private readonly OutboxRelayTests mOuter = new();

			public ValueTask DisposeAsync() => mOuter.DisposeAsync();

			[Fact]
			public async Task AnAcceptedRow_IsSubmitted_AndLosesItsEnvelope()
			{
				var id = await mOuter.addRowAsync();

				(await mOuter.relay().SubmitOneAsync(id, CancellationToken.None)).ShouldBe(RelayOutcome.Submitted);

				var row = await mOuter.rowAsync(id);
				row.State.ShouldBe(OutboxState.Submitted);
				row.SubmittedAt.ShouldBe(mOuter.mClock.GetUtcNow().UtcDateTime);
				row.Envelope.ShouldBeNull(); // the sender has its own copy now
				row.SubmitError.ShouldBeNull();
				mOuter.mTransport.Submitted.ShouldBe(["{\"to\":\"x\"}"]);
			}

			[Fact]
			public async Task ADuplicateAnswer_IsTheSameAsAccepted()
			{
				var id = await mOuter.addRowAsync();
				mOuter.mTransport.OnSubmit = (_, _) => new SubmitResult(SubmitOutcome.Duplicate);

				(await mOuter.relay().SubmitOneAsync(id, CancellationToken.None)).ShouldBe(RelayOutcome.Submitted);

				(await mOuter.rowAsync(id)).State.ShouldBe(OutboxState.Submitted);
			}

			[Fact]
			public async Task ARefusal_IsFinal_AndKeepsTheSendersReasons()
			{
				var id = await mOuter.addRowAsync();
				mOuter.mTransport.OnSubmit = (_, _) => new SubmitResult(SubmitOutcome.Rejected, "The sender refused the message (400): invalid_body: no token.");

				(await mOuter.relay().SubmitOneAsync(id, CancellationToken.None)).ShouldBe(RelayOutcome.Rejected);

				var row = await mOuter.rowAsync(id);
				row.State.ShouldBe(OutboxState.Rejected);
				row.CompletedAt.ShouldBe(mOuter.mClock.GetUtcNow().UtcDateTime);
				row.SubmitError.ShouldContain("invalid_body");
				row.Envelope.ShouldBeNull();
			}

			[Fact]
			public async Task ATransientFailure_LeavesTheRowPendingWithItsEnvelope_AndNotesWhy()
			{
				var id = await mOuter.addRowAsync();
				mOuter.mTransport.OnSubmit = (_, _) => new SubmitResult(SubmitOutcome.TransientFailure, "The sender could not be reached.");

				(await mOuter.relay().SubmitOneAsync(id, CancellationToken.None)).ShouldBe(RelayOutcome.TransportFailure);

				var row = await mOuter.rowAsync(id);
				row.State.ShouldBe(OutboxState.Pending);
				row.Envelope.ShouldNotBeNull();
				row.SubmitError.ShouldBe("The sender could not be reached.");
				row.SubmittedAt.ShouldBeNull();
			}

			[Fact]
			public async Task ARowPastItsDeadline_IsExpired_WithoutBotheringTheSender()
			{
				var id = await mOuter.addRowAsync(expiresIn: TimeSpan.FromMinutes(10));
				mOuter.mClock.Advance(TimeSpan.FromMinutes(11));

				(await mOuter.relay().SubmitOneAsync(id, CancellationToken.None)).ShouldBe(RelayOutcome.Expired);

				var row = await mOuter.rowAsync(id);
				row.State.ShouldBe(OutboxState.Expired);
				row.CompletedAt.ShouldBe(mOuter.mClock.GetUtcNow().UtcDateTime);
				row.SubmitError.ShouldContain("Expired");
				row.Envelope.ShouldBeNull();
				mOuter.mTransport.Submitted.ShouldBeEmpty();
			}

			[Fact]
			public async Task ARowWithNoEnvelope_IsRejected_NotRetriedForever()
			{
				var id = await mOuter.addRowAsync(envelope: null);

				(await mOuter.relay().SubmitOneAsync(id, CancellationToken.None)).ShouldBe(RelayOutcome.Rejected);

				(await mOuter.rowAsync(id)).State.ShouldBe(OutboxState.Rejected);
				mOuter.mTransport.Submitted.ShouldBeEmpty();
			}

			[Theory]
			[InlineData(OutboxState.Submitted)]
			[InlineData(OutboxState.Completed)]
			[InlineData(OutboxState.Rejected)]
			[InlineData(OutboxState.Expired)]
			public async Task ARowThatIsNotPending_IsLeftAlone(byte state)
			{
				var id = await mOuter.addRowAsync(state, envelope: null);

				(await mOuter.relay().SubmitOneAsync(id, CancellationToken.None)).ShouldBe(RelayOutcome.Skipped);

				mOuter.mTransport.Submitted.ShouldBeEmpty();
				(await mOuter.rowAsync(id)).State.ShouldBe(state);
			}

			[Fact]
			public async Task AnUnknownRow_IsSkipped()
			{
				(await mOuter.relay().SubmitOneAsync(424242, CancellationToken.None)).ShouldBe(RelayOutcome.Skipped);
			}

			[Fact]
			public async Task AVeryLongFailureDetail_IsCutToTheColumn()
			{
				var id = await mOuter.addRowAsync();
				mOuter.mTransport.OnSubmit = (_, _) => new SubmitResult(SubmitOutcome.TransientFailure, new string('e', 5000));

				await mOuter.relay().SubmitOneAsync(id, CancellationToken.None);

				(await mOuter.rowAsync(id)).SubmitError!.Length.ShouldBe(1000);
			}
		}

		public class Running : IAsyncDisposable
		{
			private readonly OutboxRelayTests mOuter = new();

			public ValueTask DisposeAsync() => mOuter.DisposeAsync();

			[Fact]
			public async Task RowsAreSubmittedAtOnce_WhenSignalled_NotAtTheNextPoll()
			{
				var relay = mOuter.relay(); // poll interval: a minute
				await relay.StartAsync(CancellationToken.None);
				var id = await mOuter.addRowAsync();

				mOuter.mSignal.Notify();

				await eventuallyAsync(async () => (await mOuter.rowAsync(id)).State == OutboxState.Submitted, "the row to be submitted");
			}

			[Fact]
			public async Task RowsLeftPendingBeforeStartup_AreSubmittedWhenTheRelayStarts()
			{
				var ids = new List<long>();
				for (var i = 0; i < 3; i++) ids.Add(await mOuter.addRowAsync());

				await mOuter.relay().StartAsync(CancellationToken.None);

				await eventuallyAsync(async () => await mOuter.countInStateAsync(OutboxState.Submitted) == 3, "all three rows to be submitted");
			}

			[Fact]
			public async Task ABacklogLargerThanABatch_DrainsWithoutWaitingForPolls()
			{
				for (var i = 0; i < 14; i++) await mOuter.addRowAsync();

				await mOuter.relay(o => { o.RelayBatchSize = 3; o.RelayMaxConcurrency = 2; }).StartAsync(CancellationToken.None);

				await eventuallyAsync(async () => await mOuter.countInStateAsync(OutboxState.Submitted) == 14, "all 14 rows to be submitted through 3-row batches");
				mOuter.mTransport.Submitted.Count.ShouldBe(14); // each exactly once
			}

			[Fact]
			public async Task ASenderThatIsDown_IsNotHammeredByTheWholeBacklog_AndTheBacklogGoesOutOnceItIsBack()
			{
				for (var i = 0; i < 8; i++) await mOuter.addRowAsync();
				mOuter.mTransport.OnSubmit = (_, _) => new SubmitResult(SubmitOutcome.TransientFailure, "down");

				await mOuter.relay(o => o.RelayMaxConcurrency = 2).StartAsync(CancellationToken.None);
				await eventuallyAsync(() => Task.FromResult(mOuter.mTransport.Submitted.Count >= 1), "the first attempt");
				await Task.Delay(500);

				// The gate closed after the first failures, so the rest of the backlog never tried the sender.
				mOuter.mTransport.Submitted.Count.ShouldBeLessThanOrEqualTo(2);
				(await mOuter.countInStateAsync(OutboxState.Pending)).ShouldBe(8);

				mOuter.mTransport.OnSubmit = (_, _) => new SubmitResult(SubmitOutcome.Accepted);
				mOuter.mClock.Advance(TimeSpan.FromMinutes(5));
				mOuter.mSignal.Notify();

				await eventuallyAsync(async () => await mOuter.countInStateAsync(OutboxState.Submitted) == 8, "the whole backlog to be submitted once the sender is back");
			}

			[Fact]
			public async Task ARowThatThrows_IsNotRetriedInATightLoop_AndDoesNotBlockTheOthers()
			{
				var poison = await mOuter.addRowAsync(envelope: "{\"poison\":true}");
				var good = new List<long>();
				for (var i = 0; i < 3; i++) good.Add(await mOuter.addRowAsync());
				mOuter.mTransport.OnSubmit = (envelope, _) => envelope.Contains("poison")
					? throw new InvalidOperationException("this one row cannot be processed")
					: new SubmitResult(SubmitOutcome.Accepted);

				await mOuter.relay().StartAsync(CancellationToken.None);

				await eventuallyAsync(async () => await mOuter.countInStateAsync(OutboxState.Submitted) == 3, "the good rows to go out past the poison one");
				await Task.Delay(500);
				mOuter.mTransport.Submitted.Count(e => e.Contains("poison")).ShouldBe(1); // held back, not hammered
				(await mOuter.rowAsync(poison)).State.ShouldBe(OutboxState.Pending);
			}

			[Fact]
			public async Task ARowHeldBack_IsRetriedWhenItsPauseEnds_WithoutWaitingForThePollInterval()
			{
				var id = await mOuter.addRowAsync();
				var attempts = 0;
				mOuter.mTransport.OnSubmit = (_, _) => ++attempts == 1
					? throw new InvalidOperationException("first attempt blows up")
					: new SubmitResult(SubmitOutcome.Accepted);

				await mOuter.relay().StartAsync(CancellationToken.None); // poll interval: a minute
				await eventuallyAsync(() => Task.FromResult(attempts == 1), "the first attempt");

				// The clock is moved on repeatedly rather than once: the relay records the pause just after the
				// attempt throws, so a single advance can land before it and be measured from. What is being
				// tested is that the relay's own timer notices the pause has ended; only the clock is manual.
				await eventuallyAsync(async () =>
				{
					mOuter.mClock.Advance(TimeSpan.FromSeconds(3));
					return (await mOuter.rowAsync(id)).State == OutboxState.Submitted;
				}, "the retry after the pause", timeoutMilliseconds: 15_000);
			}

			[Fact]
			public async Task StoppingReturnsPromptly()
			{
				var relay = mOuter.relay();
				await relay.StartAsync(CancellationToken.None);

				var stop = relay.StopAsync(CancellationToken.None);

				(await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5)))).ShouldBe(stop);
			}
		}

		public class Pruning : IAsyncDisposable
		{
			private readonly OutboxRelayTests mOuter = new();

			public ValueTask DisposeAsync() => mOuter.DisposeAsync();

			[Fact]
			public async Task RemovesFinishedRowsOlderThanTheRetentionWindow_AndKeepsTheRest()
			{
				var old = mOuter.mClock.GetUtcNow().UtcDateTime.AddDays(-31);
				var recent = mOuter.mClock.GetUtcNow().UtcDateTime.AddDays(-29);
				var oldCompleted = await mOuter.addRowAsync(OutboxState.Completed, null, completedAt: old, tag: "old-completed");
				var oldRejected = await mOuter.addRowAsync(OutboxState.Rejected, null, completedAt: old, tag: "old-rejected");
				var oldExpired = await mOuter.addRowAsync(OutboxState.Expired, null, completedAt: old, tag: "old-expired");
				var recentCompleted = await mOuter.addRowAsync(OutboxState.Completed, null, completedAt: recent, tag: "recent");
				var pending = await mOuter.addRowAsync(OutboxState.Pending, tag: "pending");
				var submitted = await mOuter.addRowAsync(OutboxState.Submitted, null, tag: "submitted");

				var removed = await mOuter.relay(o => o.OutboxRetentionDays = 30).PruneOnceAsync(CancellationToken.None);

				removed.ShouldBe(3);
				var remaining = await mOuter.mDatabase.QueryAsync(db => db.Set<OutboxMessage>().Select(o => o.Id).ToListAsync());
				remaining.ShouldBe([recentCompleted, pending, submitted], ignoreOrder: true);
				remaining.ShouldNotContain(oldCompleted);
				remaining.ShouldNotContain(oldRejected);
				remaining.ShouldNotContain(oldExpired);
			}

			[Fact]
			public async Task NeverTouchesAPendingOrSubmittedRow_HoweverOld()
			{
				await mOuter.addRowAsync(OutboxState.Pending);
				await mOuter.addRowAsync(OutboxState.Submitted, null);
				mOuter.mClock.Advance(TimeSpan.FromDays(400));

				(await mOuter.relay().PruneOnceAsync(CancellationToken.None)).ShouldBe(0);
			}
		}
	}
}
