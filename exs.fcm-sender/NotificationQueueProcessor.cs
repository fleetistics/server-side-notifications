using exs.Database.Commons.Interfaces;
using exs.fcm_sender.Fcm;
using exs.modelCommons.AppStructure;
using exs.modelCommons.UserManagement;
using exs.notifications_model.Transports;
using Microsoft.EntityFrameworkCore;

namespace exs.fcm_sender
{
	/// <summary>
	/// Resolves one NotificationQueue row (TransportType = FCM) to the user's currently-active
	/// mobile sessions, sends to each (with in-memory retry via ResilientFcmSender), then writes the
	/// terminal NotificationSent + one FCMNotificationSent per session attempted, and removes the
	/// queue row. Scoped: owns its own IRepository/DbContext, one instance per queue row processed.
	/// </summary>
	public sealed class NotificationQueueProcessor
	{
		public NotificationQueueProcessor(IRepository repository, ResilientFcmSender sender, ILogger<NotificationQueueProcessor> logger)
		{
			mRepository = repository;
			mSender = sender;
			mLogger = logger;
		}

		public async Task ProcessAsync(FcmQueue queueItem, CancellationToken cancellationToken)
		{
			if (queueItem.Notification == null)
			{
				throw new InvalidOperationException($"NotificationQueue #{queueItem.Id} has no loaded Notification (dangling NotificationId {queueItem.NotificationId}?).");
			}

			// Only UserId/StatusId are filtered in the query itself - both plain scalars on
			// UserSession. Platform/FCMToken live on the owned ClientInfo type, and matching on those
			// is left to LINQ-to-Objects below rather than folded into the same DB predicate: a user
			// has at most a handful of sessions, so filtering the rest in memory costs nothing here,
			// and it keeps this query from depending on a provider's ability to translate member
			// access through an owned type inside a boolean expression.
			var candidateSessions = await mRepository.GetQueryable<UserSession>(s =>
					s.UserId == queueItem.UserId &&
					s.StatusId == UserSessionStatus.Active)
				.AsNoTracking()
				.ToListAsync(cancellationToken);

			var activeSessions = candidateSessions
				.Where(s =>
					(s.ClientInfo.PlatformId == ClientDevicePlatform.Ios || s.ClientInfo.PlatformId == ClientDevicePlatform.Android) &&
					!string.IsNullOrEmpty(s.ClientInfo.FCM_FID))
				.ToList();

			bool hasProcessedSessions = false;
			if (queueItem.IsProcessing)
			{
				var processedSessionsIds = await mRepository.GetQueryable<FcmSent>(e => e.NotificationId == queueItem.NotificationId && e.UserId == queueItem.UserId && e.UserSessionId != null && e.FcmToken != null && e.Error == null)
					.Select(e => e.UserSessionId)
					.ToListAsync(cancellationToken);
				if (processedSessionsIds.Count > 0)
				{
					activeSessions = activeSessions.Where(s => !processedSessionsIds.Any(id => id == s.Id)).ToList();
					hasProcessedSessions = true;
				}
			}

			if (!activeSessions.Any())
			{
				mLogger.LogInformation(
					"No active sessions for NotificationQueue #{QueueId} (Notification #{NotificationId}, User #{UserId}) - marking as sent with no attempts.",
					queueItem.Id, queueItem.NotificationId, queueItem.UserId);
				if (!hasProcessedSessions)
				{
					mRepository.Create(new FcmSent
					{
						NotificationId = queueItem.NotificationId,
						UserId = queueItem.UserId,
						Date = DateTime.UtcNow,
						Error = "No active sessions",
					});
				}
				mRepository.DeleteAll<FcmQueue>(q => q.Id == queueItem.Id);
				await mRepository.SaveAsync(cancellationToken);

				return;
			}

			var title = queueItem.Notification.Title ?? "";
			var body = queueItem.Notification.Body ?? "";
			var payload = queueItem.Notification.Payload ?? "";

			if (!queueItem.IsProcessing)
			{
				mRepository.Attach(queueItem);
				queueItem.IsProcessing = true;
				await mRepository.SaveAsync(cancellationToken);
			}

			foreach (var session in activeSessions)
			{
				var (result, attemptCount) = await mSender.SendWithRetryAsync(
					session.ClientInfo.FCM_FID, session.ClientInfo.PlatformId, title, body,
					queueItem.Notification.TypeId, queueItem.Notification.EntityId, payload, cancellationToken);

				if (result.Outcome != FcmSendOutcome.Ok)
				{
					mLogger.LogWarning(
						"FCM send failed for NotificationQueue #{QueueId}, session #{SessionId}, after {Attempts} attempt(s): {ErrorCode} {ErrorMessage}",
						queueItem.Id, session.Id, attemptCount, result.ErrorCode, result.ErrorMessage);
				}

				mRepository.Create(new FcmSent
				{
					UserSessionId = session.Id,
					FcmToken = session.ClientInfo.FCM_FID,
					Error = result.Outcome == FcmSendOutcome.Ok ? null : result.ErrorMessage,
					Date = DateTime.UtcNow,
					NotificationId = queueItem.NotificationId,
					UserId = queueItem.UserId,
				});
				await mRepository.SaveAsync(cancellationToken); // save each attempt as we go, so we don't lose them if the process crashes mid-batch
			}
			mRepository.DeleteAll<FcmQueue>(q => q.Id == queueItem.Id);
			await mRepository.SaveAsync(cancellationToken);
		}

		private readonly IRepository mRepository;
		private readonly ResilientFcmSender mSender;
		private readonly ILogger<NotificationQueueProcessor> mLogger;
	}
}
