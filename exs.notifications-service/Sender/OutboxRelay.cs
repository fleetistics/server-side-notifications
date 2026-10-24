using System.Collections.Concurrent;
using System.Threading.Channels;
using exs.Database.Commons.Interfaces;
using exs.notifications_contracts;
using exs.notifications_model.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace exs.notifications_service.Sender
{
	public enum RelayOutcome
	{
		/// <summary>Nothing to do: no longer pending, or not attempted because the sender is being given a rest.</summary>
		Skipped,

		/// <summary>The sender accepted it (or already had it).</summary>
		Submitted,

		/// <summary>The sender refused it for good.</summary>
		Rejected,

		/// <summary>It ran out of time before the sender accepted it.</summary>
		Expired,

		/// <summary>The sender could not be reached or would not take it. The row stays pending.</summary>
		TransportFailure,
	}

	/// <summary>
	/// Hands pending outbox rows to the sender. A poller puts pending ids on an in-memory queue and a fixed
	/// set of workers submit them one at a time, so one slow submission delays only its own worker.
	///
	/// Two things keep a failing sender from making things worse. A failed submission closes a gate on all
	/// submissions for a while, so a backlog does not each wait out its own timeout; and a row whose
	/// processing throws is held back on its own, so it cannot starve the rows behind it.
	///
	/// Safe to run in more than one host instance: the sender deduplicates on MessageId, so two relays
	/// submitting the same row is answered "duplicate", not sent twice.
	/// </summary>
	public sealed class OutboxRelay : BackgroundService
	{
		private readonly IServiceScopeFactory mScopeFactory;
		private readonly INotificationTransport mTransport;
		private readonly SenderTransportOptions mOptions;
		private readonly OutboxSignal mSignal;
		private readonly TimeProvider mClock;
		private readonly ILogger<OutboxRelay> mLogger;
		private readonly string mChannel;
		private readonly ConcurrentDictionary<long, byte> mInFlight = new();
		private readonly RowCooldown mRowCooldown;
		private readonly TransportGate mGate;
		private DateTimeOffset mLastPrune = DateTimeOffset.MinValue;

		public OutboxRelay(IServiceScopeFactory scopeFactory, INotificationTransport transport, IOptions<SenderTransportOptions> options, OutboxSignal signal, TimeProvider clock, ILogger<OutboxRelay> logger, string channel = Channels.Fcm)
		{
			mChannel = channel;
			mScopeFactory = scopeFactory;
			mTransport = transport;
			mOptions = options.Value;
			mSignal = signal;
			mClock = clock;
			mLogger = logger;
			var max = TimeSpan.FromSeconds(mOptions.RelayBackoffMaxSeconds);
			mRowCooldown = new RowCooldown(max, clock);
			mGate = new TransportGate(max, clock);
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			await Task.Yield();

			var queue = Channel.CreateBounded<long>(new BoundedChannelOptions(mOptions.RelayBatchSize)
			{
				SingleWriter = true,
				FullMode = BoundedChannelFullMode.Wait,
			});
			var workers = Enumerable.Range(0, mOptions.RelayMaxConcurrency)
				.Select(_ => Task.Run(() => workAsync(queue.Reader, stoppingToken), CancellationToken.None))
				.ToArray();

			mLogger.LogInformation("Outbox relay for {Channel} started: batch {BatchSize}, {Workers} workers, sender {BaseUrl}.", mChannel, mOptions.RelayBatchSize, mOptions.RelayMaxConcurrency, mOptions.BaseUrl);

			try
			{
				while (!stoppingToken.IsCancellationRequested)
				{
					try
					{
						if (!mGate.IsBlocked)
						{
							await enqueuePendingAsync(queue.Writer, stoppingToken);
						}
						await pruneIfDueAsync(stoppingToken);
					}
					catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
					{
						break;
					}
					catch (Exception ex)
					{
						mLogger.LogError(ex, "Error looking for outbox rows to submit.");
					}

					await mSignal.WaitAsync(nextWait(), stoppingToken);
				}
			}
			finally
			{
				queue.Writer.TryComplete();
				await Task.WhenAll(workers);
			}
		}

		/// <summary>The fallback poll, cut short when a backoff (the whole sender's, or one row's) ends sooner.</summary>
		private TimeSpan nextWait()
		{
			var wait = TimeSpan.FromSeconds(mOptions.RelayPollIntervalSeconds);
			foreach (var sooner in new[] { mGate.TimeUntilOpen(), mRowCooldown.TimeUntilNextExpiry() })
			{
				if (sooner is { } untilThen && untilThen < wait)
				{
					// A little past the end of the backoff, so the wake-up does not land a hair before it.
					wait = untilThen + TimeSpan.FromMilliseconds(50);
				}
			}
			return wait;
		}

		private async Task enqueuePendingAsync(ChannelWriter<long> writer, CancellationToken cancellationToken)
		{
			using var scope = mScopeFactory.CreateScope();
			var repository = scope.ServiceProvider.GetRequiredService<IRepository>();

			// Rows already queued, being submitted, or held back are left out of the query itself, so they are
			// neither handed out twice nor allowed to crowd newer rows out of the batch.
			var skip = new HashSet<long>(mInFlight.Keys);
			skip.UnionWith(mRowCooldown.CoolingIds());
			var skipList = skip.ToList();

			var ids = await repository.GetQueryable<OutboxMessage>(o => o.Channel == mChannel && o.State == OutboxState.Pending && !skipList.Contains(o.Id))
				.AsNoTracking()
				.OrderBy(o => o.CreatedAt)
				.Select(o => o.Id)
				.Take(mOptions.RelayBatchSize)
				.ToListAsync(cancellationToken);

			foreach (var id in ids)
			{
				if (mInFlight.TryAdd(id, 0))
				{
					await writer.WriteAsync(id, cancellationToken);
				}
			}
		}

		private async Task workAsync(ChannelReader<long> reader, CancellationToken stoppingToken)
		{
			try
			{
				await foreach (var id in reader.ReadAllAsync(stoppingToken))
				{
					try
					{
						await processAsync(id, stoppingToken);
					}
					finally
					{
						mInFlight.TryRemove(id, out _);
						mSignal.Notify();
					}
				}
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
			}
		}

		private async Task processAsync(long id, CancellationToken stoppingToken)
		{
			try
			{
				// The gate may have closed while this id waited in the queue. Skipping it leaves the row
				// pending, so it is picked up again when the gate opens.
				if (mGate.IsBlocked) return;

				var outcome = await SubmitOneAsync(id, stoppingToken);
				switch (outcome)
				{
					case RelayOutcome.TransportFailure:
						mGate.RegisterFailure();
						break;
					case RelayOutcome.Submitted:
					case RelayOutcome.Rejected:
						// The sender answered, so it is up, whatever it thought of this message.
						mGate.RegisterSuccess();
						mRowCooldown.Clear(id);
						break;
					default:
						mRowCooldown.Clear(id);
						break;
				}
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				mLogger.LogError(ex, "Error submitting outbox row {Id}; it stays pending and is retried after a pause.", id);
				mRowCooldown.RegisterFault(id);
			}
		}

		/// <summary>Takes one pending row as far as it can go. Internal so tests can drive one row at a time.</summary>
		internal async Task<RelayOutcome> SubmitOneAsync(long id, CancellationToken cancellationToken)
		{
			using var scope = mScopeFactory.CreateScope();
			var repository = scope.ServiceProvider.GetRequiredService<IRepository>();

			var row = await repository.GetQueryable<OutboxMessage>(o => o.Id == id && o.Channel == mChannel && o.State == OutboxState.Pending).SingleOrDefaultAsync(cancellationToken);
			if (row == null) return RelayOutcome.Skipped;

			var now = mClock.GetUtcNow().UtcDateTime;

			if (row.ExpiresAt <= now)
			{
				finish(row, OutboxState.Expired, now, "Expired before the sender accepted it.");
				await repository.SaveAsync(cancellationToken);
				return RelayOutcome.Expired;
			}

			if (string.IsNullOrEmpty(row.Envelope))
			{
				// Cannot happen for a row this relay wrote. If it ever does, retrying cannot help.
				finish(row, OutboxState.Rejected, now, "The row has no envelope to send.");
				await repository.SaveAsync(cancellationToken);
				return RelayOutcome.Rejected;
			}

			var result = await mTransport.SubmitAsync(row.Envelope, cancellationToken);

			switch (result.Outcome)
			{
				case SubmitOutcome.Accepted:
				case SubmitOutcome.Duplicate:
					row.State = OutboxState.Submitted;
					row.SubmittedAt = now;
					row.SubmitError = null;
					row.Envelope = null; // the sender has its own copy now
					await repository.SaveAsync(cancellationToken);
					return RelayOutcome.Submitted;

				case SubmitOutcome.Rejected:
					mLogger.LogWarning("The sender refused outbox row {Id} (message {MessageId}): {Detail}", row.Id, row.MessageId, result.Detail);
					finish(row, OutboxState.Rejected, now, result.Detail);
					await repository.SaveAsync(cancellationToken);
					return RelayOutcome.Rejected;

				default:
					mLogger.LogWarning("Could not submit outbox row {Id} to the sender: {Detail}", row.Id, result.Detail);
					row.SubmitError = truncate(result.Detail);
					await repository.SaveAsync(cancellationToken);
					return RelayOutcome.TransportFailure;
			}
		}

		private static void finish(OutboxMessage row, byte state, DateTime now, string? error)
		{
			row.State = state;
			row.CompletedAt = now;
			row.SubmitError = truncate(error);
			row.Envelope = null;
		}

		private async Task pruneIfDueAsync(CancellationToken cancellationToken)
		{
			var now = mClock.GetUtcNow();
			if (now - mLastPrune < TimeSpan.FromHours(1)) return;
			mLastPrune = now;
			await PruneOnceAsync(cancellationToken);
		}

		/// <summary>Removes finished rows older than the retention window. Pending and submitted rows are never touched.</summary>
		internal async Task<int> PruneOnceAsync(CancellationToken cancellationToken)
		{
			var cutoff = mClock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(mOptions.OutboxRetentionDays);

			using var scope = mScopeFactory.CreateScope();
			var repository = scope.ServiceProvider.GetRequiredService<IRepository>();

			var removed = await repository.GetQueryable<OutboxMessage>(o => o.Channel == mChannel && o.State >= OutboxState.Completed && o.CompletedAt < cutoff)
				.ExecuteDeleteAsync(cancellationToken);

			if (removed > 0)
			{
				mLogger.LogInformation("Pruned {Count} finished {Channel} outbox rows older than {Days} days.", removed, mChannel, mOptions.OutboxRetentionDays);
			}
			return removed;
		}

		private static string? truncate(string? value) => value is { Length: > 1000 } ? value[..1000] : value;
	}
}
