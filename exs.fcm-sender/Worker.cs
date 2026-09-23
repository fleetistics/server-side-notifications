using exs.Database.Commons.Interfaces;
using exs.notifications_model.Transports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace exs.fcm_sender
{
	/// <summary>
	/// Polls NotificationQueue for TransportType = FCM and drains it. Each queue row is processed in
	/// its own DI scope (own DbContext) so rows within a batch can run concurrently, bounded by
	/// MaxConcurrency - NotificationQueueProcessor does the actual per-row work.
	/// </summary>
	public sealed class Worker : BackgroundService
	{
		public Worker(IServiceScopeFactory scopeFactory, IOptions<FcmSenderOptions> options, ILogger<Worker> logger)
		{
			mScopeFactory = scopeFactory;
			mOptions = options.Value;
			mQueueExpiration = TimeSpan.FromMinutes(mOptions.QueueExpirationMinutes);
			mLogger = logger;
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			mLogger.LogInformation(
				"FCM sender starting: batch size {BatchSize}, max concurrency {MaxConcurrency}, poll interval {PollIntervalSeconds}s",
				mOptions.BatchSize, mOptions.MaxConcurrency, mOptions.PollIntervalSeconds);

			while (!stoppingToken.IsCancellationRequested)
			{
				bool processedAny;
				try
				{
					processedAny = await pollOnceAsync(stoppingToken);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					return;
				}
				catch (Exception ex)
				{
					mLogger.LogError(ex, "Error polling notification_queue");
					processedAny = false;
				}

				if (!processedAny)
				{
					await Task.Delay(TimeSpan.FromSeconds(mOptions.PollIntervalSeconds), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
				}
			}
		}

		// Internal (rather than private) so tests can drive a single poll pass deterministically
		// instead of racing the real ExecuteAsync loop's timing.
		internal async Task<bool> pollOnceAsync(CancellationToken stoppingToken)
		{
			List<FcmQueue> batch;
			using (var scope = mScopeFactory.CreateScope())
			{
				var db = scope.ServiceProvider.GetRequiredService<IRepository>();
				batch = await db.GetQueryable<FcmQueue>()
					.Include(q => q.Notification)
					.OrderBy(q => q.Date)
					.Take(mOptions.BatchSize)
					.ToListAsync(stoppingToken);

				var now = DateTime.UtcNow;
				var expired = batch.Where(q => q.Date < now - mQueueExpiration).ToList();
				if (expired.Count > 0)
				{
					foreach (var q in expired)
					{
						db.Create(new FcmSent
						{
							Date = now,
							NotificationId = q.NotificationId,
							UserId = q.UserId,
							ErrorCode = FcmSentErrorCode.Expired,
							Error = "Expired",
						});
						db.Delete(q);
					}
					await db.SaveAsync(stoppingToken);
					batch = batch.Except(expired).ToList();
				}
			}

			if (batch.Count == 0) return false;

			await Parallel.ForEachAsync(
				batch,
				new ParallelOptions { MaxDegreeOfParallelism = mOptions.MaxConcurrency, CancellationToken = stoppingToken },
				async (queueItem, ct) =>
				{
					using var scope = mScopeFactory.CreateScope();
					var processor = scope.ServiceProvider.GetRequiredService<NotificationQueueProcessor>();
					try
					{
						await processor.ProcessAsync(queueItem, ct);
					}
					catch (Exception ex) when (!(ex is OperationCanceledException))
					{
						// One bad row (e.g. a transient DB error while saving the result) shouldn't
						// stop the rest of the batch - it stays in NotificationQueue and is retried
						// next poll.
						mLogger.LogError(ex, "Error processing NotificationQueue #{QueueId}", queueItem.Id);
					}
				});

			return true;
		}

		private readonly TimeSpan mQueueExpiration;
		private readonly IServiceScopeFactory mScopeFactory;
		private readonly FcmSenderOptions mOptions;
		private readonly ILogger<Worker> mLogger;
	}
}
