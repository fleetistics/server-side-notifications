using exs.Database.Commons.Interfaces;
using exs.notifications_model.Notifications;
using exs.notifications_service.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace exs.notifications_service.Impl
{
	public class NotificationService: BackgroundService, INotificationService
	{
		public NotificationService(IServiceScopeFactory scopeFactory, IEnumerable<ITransportNotificationProcessor> transportNotificationProcessors, IOptions<NotificationServiceOptions> options, ILogger<NotificationService> logger)
		{
			mScopeFactory = scopeFactory;
			mTransportNotificationProcessors = transportNotificationProcessors;
			mLogger = logger;

			var serviceOptions = options.Value;
			mExpirationInterval = TimeSpan.FromMinutes(serviceOptions.ExpirationMinutes);
			mSweepInterval = TimeSpan.FromSeconds(serviceOptions.SweepIntervalSeconds);
			mSweepGracePeriod = TimeSpan.FromSeconds(serviceOptions.SweepGracePeriodSeconds);

			// Built here rather than in a field initializer because it needs mLogger. DropOldest means
			// TryWrite always reports success even when it has just evicted something, so without the
			// itemDropped callback a full queue loses notifications completely silently - the drop is
			// recoverable (the row is still Status=New and the sweep will find it) but a queue that is
			// filling up at all means the reader is falling behind, which is worth knowing about.
			mNotificationQueue = Channel.CreateBounded<NotificationQueueItem>(
				new BoundedChannelOptions(QUEUE_CAPACITY)
				{
					SingleReader = true,
					SingleWriter = false,
					FullMode = BoundedChannelFullMode.DropOldest,
				},
				dropped => mLogger.LogWarning(
					"In-memory notification queue is full ({Capacity}) - dropped notification {NotificationId}. It stays Status=New; the periodic sweep will pick it up within {SweepInterval}.",
					QUEUE_CAPACITY, dropped.NotificationId, mSweepInterval));
		}

		public override async Task StopAsync(CancellationToken cancellationToken)
		{
			mNotificationQueue.Writer.TryComplete();
			await base.StopAsync(cancellationToken);
		}

		public bool EnqueueNotification(int notificationId, List<int> userIds)
		{
			var result = mNotificationQueue.Writer.TryWrite(new NotificationQueueItem
			{
				NotificationId = notificationId,
				UserIds = userIds
			});
			if (!result)
			{
				// Deliberately not "queue is full": with DropOldest, TryWrite only ever fails once the
				// writer has been completed, which happens in StopAsync. A full queue drops silently and
				// is reported by the itemDropped callback instead.
				mLogger.LogWarning("Notification {NotificationId} for users {UserIds} was not enqueued - the service is shutting down. It stays Status=New and the next startup sweep will process it.", notificationId, string.Join(", ", userIds));
			}
			return result;
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			await Task.Yield(); // Ensure the method is asynchronous

			await processNotificationsTable(stoppingToken);
			var nextSweepDue = DateTime.UtcNow + mSweepInterval;

			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					var channelStillOpen = await waitForWorkOrSweepAsync(nextSweepDue, stoppingToken);

					var items = new List<NotificationQueueItem>();
					while (mNotificationQueue.Reader.TryRead(out var item))
					{
						items.Add(item);
					}

					if (items.Any())
					{
						using var scope = mScopeFactory.CreateScope();
						var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
						await processNotificationsQueue(repository, items, stoppingToken);
					}

					// Runs on this same loop, after the drain above, rather than on its own timer. That
					// is what stops it racing the channel: a notification cannot be handed to the
					// transport twice (once from the queue, once from the sweep) because the two paths
					// can never be in flight at the same time.
					if (DateTime.UtcNow >= nextSweepDue)
					{
						await processNotificationsTable(stoppingToken);
						nextSweepDue = DateTime.UtcNow + mSweepInterval;
					}

					// StopAsync completed the writer. Everything still buffered was just drained, and
					// waiting on a completed channel returns immediately, so continuing would spin until
					// the stopping token caught up.
					if (!channelStillOpen)
					{
						break;
					}
				}
				catch (OperationCanceledException)
				{
					// Graceful shutdown
					break;
				}
				catch (Exception ex)
				{
					mLogger.LogError(ex, "Error occurred while processing notifications.");
					try
					{
						await Task.Delay(FAILURE_DELAY, stoppingToken); // Delay before retrying
					}
					catch (OperationCanceledException)
					{
						break;
					}
				}
			}
		}

		/// <summary>
		/// Blocks until there is something in the queue or the next sweep falls due, whichever happens
		/// first. Returns false once the channel has been completed by StopAsync.
		/// </summary>
		private async Task<bool> waitForWorkOrSweepAsync(DateTime nextSweepDue, CancellationToken stoppingToken)
		{
			var untilSweep = nextSweepDue - DateTime.UtcNow;
			if (untilSweep <= TimeSpan.Zero)
			{
				return true; // already overdue - drain whatever is there and sweep immediately
			}

			// A linked source rather than Task.WhenAny over a Task.Delay: whichever way this wakes up,
			// the WaitToReadAsync has to be cancelled rather than abandoned. The channel is SingleReader,
			// so leaving orphaned waiters queued on it every interval is not something to risk.
			using var wakeUp = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
			wakeUp.CancelAfter(untilSweep);
			try
			{
				return await mNotificationQueue.Reader.WaitToReadAsync(wakeUp.Token);
			}
			catch (OperationCanceledException)
			{
				stoppingToken.ThrowIfCancellationRequested(); // real shutdown - let the loop's handler end it
				return true; // just the sweep timer firing, which is an ordinary wake-up
			}
		}

		/// <summary>
		/// Reconciles against the notification table: anything still Status=New that the in-memory queue
		/// did not deliver gets picked up here. Runs at startup and then on the configured sweep interval
		/// from the reader loop, which is what makes the channel's DropOldest survivable - a dropped
		/// notification is a delay of at most one interval rather than a permanent loss, and is recovered
		/// well inside the expiry window instead of eventually being marked Expired unsent.
		/// It also picks up rows written by anything other than this process.
		///
		/// Internal rather than private, on the same reasoning as Worker.pollOnceAsync: tests can then
		/// drive one sweep deterministically instead of starting the real loop and polling the database
		/// until something happens, which against a single shared SQLite connection is both slow and
		/// intermittently "database is locked".
		/// </summary>
		internal async Task processNotificationsTable(CancellationToken stoppingToken)
		{
			using var scope = mScopeFactory.CreateScope();
			var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
			// The grace period keeps the sweep off notifications that were only just committed and are
			// still sitting in the in-memory queue waiting to be drained, which would otherwise be
			// fanned out twice. Combined with running on the reader loop this closes the race in
			// practice; it is not a hard guarantee, since a batch that took longer than the grace period
			// to process could leave an older item still queued. Only claiming rows at selection time
			// (see the FCM worker's missing claim mechanism) would make it airtight.
			var sweepHorizon = DateTime.UtcNow - mSweepGracePeriod;
			var notifications = await repository.GetQueryable<Notification>()
				.Where(n => n.Status == NotificationStatus.New && n.Date < sweepHorizon)
				.ToListAsync(stoppingToken);
			if (!notifications.Any())
			{
				// Debug, not Information: this is the normal outcome of every sweep on a healthy system.
				mLogger.LogDebug("No unprocessed notifications found.");
				return;
			}
			var now = DateTime.UtcNow;
			var expiredNotifications = notifications.Where(n => n.Date < now - mExpirationInterval).ToList();
			if (expiredNotifications.Any())
			{
				foreach (var expiredNotification in expiredNotifications)
				{
					expiredNotification.Status = NotificationStatus.Expired;
				}
				await repository.SaveAsync(stoppingToken);
				mLogger.LogInformation("Marked {Count} notifications as expired.", expiredNotifications.Count);
				notifications = notifications.Except(expiredNotifications).ToList();
			}
			if (!notifications.Any())
			{
				mLogger.LogDebug("No unprocessed notifications found.");
				return;
			}

			mLogger.LogInformation("Sweep found {Count} unprocessed notification(s) the in-memory queue did not deliver.", notifications.Count);

			var notificationsIds = notifications.Select(n => n.Id).ToList();
			var notificationsToUsers = await repository.GetQueryable<NotificationToUser>()
				.Where(ntu => notificationsIds.Contains(ntu.NotificationId))
				.AsNoTracking()
				.ToListAsync(stoppingToken);

			var items = notifications.Select(n => new NotificationQueueItem
			{
				NotificationId = n.Id,
				UserIds = notificationsToUsers.Where(ntu => ntu.NotificationId == n.Id).Select(ntu => ntu.UserId).ToList()
			}).ToList();

			await processNotificationsQueue(repository, items, stoppingToken);
		}

		private async Task processNotificationsQueue(IRepository repository, List<NotificationQueueItem> queueItems, CancellationToken stoppingToken)
		{

			var transportProcessor = mTransportNotificationProcessors.FirstOrDefault(tp => tp.TransportType == NotificationTransportType.FCM);
			if (transportProcessor == null)
			{
				mLogger.LogWarning("No transport processor found for FCM.");
				return;
			}
			var ids = new List<int>(queueItems.Count);
			foreach (var queueItem in queueItems)
			{
				try
				{
					stoppingToken.ThrowIfCancellationRequested();
					await transportProcessor.ProcessNotificationAsync(repository, queueItem.NotificationId, queueItem.UserIds, stoppingToken);
					await repository.SaveAsync(stoppingToken);
					ids.Add(queueItem.NotificationId);
				}
				catch (Exception ex)
				{
					mLogger.LogError(ex, "Error processing notification {NotificationId}.", queueItem.NotificationId);
				}
			}
			if (ids.Count > 0)
			{
				// Deliberately NOT passed stoppingToken, unlike every other await in this method. Do not
				// "fix" it by adding one back.
				//
				// Every id in this list has already had its fcm_queue rows committed, so those pushes are
				// going to be delivered whatever happens next. This statement is only the record of that.
				// Cancelling it during shutdown leaves the notification at Status=New with its queue rows
				// live, so the next startup sweep fans it out a second time and every recipient gets the
				// push twice - and since the per-item catch above swallows the cancellation and lets the
				// loop run on, that would happen to every notification in the batch that had succeeded,
				// not just the one in flight. Every deployment is a shutdown, so this is routine rather
				// than a corner case.
				//
				// The cost of finishing is one short indexed UPDATE holding shutdown up. Pinned by
				// Sweep_ShutdownMidBatch_StillRecordsWhatAlreadySucceeded.
				await repository.GetQueryable<Notification>(n => ids.Contains(n.Id))
					.ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, NotificationStatus.Processed));
			}
		}

		private class NotificationQueueItem
		{
			public int NotificationId { get; set; }
			public List<int> UserIds { get; set; } = null!;
		}

		private readonly TimeSpan FAILURE_DELAY = TimeSpan.FromSeconds(5);
		private const int QUEUE_CAPACITY = 100;

		// All three from NotificationServiceOptions - see it for what each one trades off.
		private readonly TimeSpan mExpirationInterval;
		private readonly TimeSpan mSweepInterval;
		private readonly TimeSpan mSweepGracePeriod;

		private readonly Channel<NotificationQueueItem> mNotificationQueue;
		private readonly IServiceScopeFactory mScopeFactory;
		private readonly IEnumerable<ITransportNotificationProcessor> mTransportNotificationProcessors;
		private readonly ILogger<NotificationService> mLogger;
	}
}
