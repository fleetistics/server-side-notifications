using exs.Database.Commons.Interfaces;
using exs.notifications_model.Notifications;
using exs.notifications_service.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace exs.notifications_service.Impl
{
	public class NotificationService: BackgroundService, INotificationService
	{
		public NotificationService(IServiceScopeFactory scopeFactory, IEnumerable<ITransportNotificationProcessor> transportNotificationProcessors, ILogger<NotificationService> logger)
		{
			mScopeFactory = scopeFactory;
			mTransportNotificationProcessors = transportNotificationProcessors;
			mLogger = logger;
		}

		public override async Task StopAsync(CancellationToken cancellationToken)
		{
			mNotificationQueue.Writer.TryComplete();
			await base.StopAsync(cancellationToken);
		}

		public bool EnqueueNotification(Notification notification, List<int> userIds)
		{
			var result = mNotificationQueue.Writer.TryWrite(new NotificationQueueItem
			{
				Notification = notification,
				UserIds = userIds
			});
			if (!result)
			{
				mLogger.LogWarning("Failed to enqueue notification {NotificationId} for users: {UserIds}. Queue is full.", notification.Id, string.Join(", ", userIds));
			}
			return result;
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			await Task.Yield(); // Ensure the method is asynchronous

			await processNotificationsTable(stoppingToken);

			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					await mNotificationQueue.Reader.WaitToReadAsync(stoppingToken);
					var items = new List<NotificationQueueItem>();
					while (mNotificationQueue.Reader.TryRead(out var item))
					{
						items.Add(item);
					}

					if (!items.Any())
					{
						continue;
					}

					using (var scope = mScopeFactory.CreateScope())
					{
						var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
						await processNotificationsQueue(repository, items, stoppingToken);
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

		private async Task processNotificationsTable(CancellationToken stoppingToken)
		{
			using var scope = mScopeFactory.CreateScope();
			var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
			var notifications = await repository.GetQueryable<Notification>().Where(n => n.Status == NotificationStatus.New).ToListAsync(stoppingToken);
			if (!notifications.Any())
			{
				mLogger.LogInformation("No unprocessed notifications found.");
				return;
			}
			var now = DateTime.UtcNow;
			var expiredNotifications = notifications.Where(n => n.Date < now - EXPIRATION_INTERVAL).ToList();
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
				mLogger.LogInformation("No unprocessed notifications found.");
				return;
			}

			var notificationsIds = notifications.Select(n => n.Id).ToList();
			var notificationsToUsers = await repository.GetQueryable<NotificationToUser>()
				.Where(ntu => notificationsIds.Contains(ntu.NotificationId))
				.AsNoTracking()
				.ToListAsync(stoppingToken);

			var items = notifications.Select(n => new NotificationQueueItem
			{
				Notification = n,
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
			foreach (var queueItem in queueItems)
			{
				try
				{
					stoppingToken.ThrowIfCancellationRequested();
					await transportProcessor.ProcessNotificationAsync(repository, queueItem.Notification, queueItem.UserIds, stoppingToken);
					// Attach is a no-op when this came from the startup catch-up pass (already tracked
					// by this same repository) but is required for a live-enqueued notification: it was
					// created and saved through the caller's own DbContext, so this call's freshly
					// scoped repository has never seen it, and mutating Status below would otherwise be
					// silently lost instead of persisted.
					repository.Attach(queueItem.Notification);
					queueItem.Notification.Status = NotificationStatus.Processed;
					await repository.SaveAsync(stoppingToken);
					mLogger.LogInformation("Processed notification {NotificationId} for users: {UserIds}.", queueItem.Notification.Id, string.Join(", ", queueItem.UserIds));
				}
				catch (Exception ex)
				{
					mLogger.LogError(ex, "Error processing notification {NotificationId}.", queueItem.Notification.Id);
				}
			}
		}

		private class NotificationQueueItem
		{
			public Notification Notification { get; set; } = null!;
			public List<int> UserIds { get; set; } = null!;
		}

		private readonly TimeSpan FAILURE_DELAY = TimeSpan.FromSeconds(5);
		private readonly TimeSpan EXPIRATION_INTERVAL = TimeSpan.FromMinutes(30);

		private Channel<NotificationQueueItem> mNotificationQueue = Channel.CreateBounded<NotificationQueueItem>(new BoundedChannelOptions(100)
		{
			SingleReader = true,
			SingleWriter = false,
			FullMode = BoundedChannelFullMode.DropOldest,
		});
		private readonly IServiceScopeFactory mScopeFactory;
		private readonly IEnumerable<ITransportNotificationProcessor> mTransportNotificationProcessors;
		private readonly ILogger<NotificationService> mLogger;
	}
}
