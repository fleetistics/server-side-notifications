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
						ErrorCode = FcmSentErrorCode.NoActiveSessions,
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
					// ErrorCode and ProviderMessageId are mutually exclusive by construction: FcmSendResult
					// carries a code only on failure and a message id only on success.
					ErrorCode = result.ErrorCode,
					Error = result.Outcome == FcmSendOutcome.Ok ? null : result.ErrorMessage,
					ProviderMessageId = result.ProviderMessageId,
					Date = DateTime.UtcNow,
					NotificationId = queueItem.NotificationId,
					UserId = queueItem.UserId,
				});
				await mRepository.SaveAsync(cancellationToken); // save each attempt as we go, so we don't lose them if the process crashes mid-batch

				if (result.TokenRejected)
				{
					// After the save above, so the delivery record survives even if this housekeeping does not.
					await clearRejectedTokenAsync(session, cancellationToken);
				}
			}
			mRepository.DeleteAll<FcmQueue>(q => q.Id == queueItem.Id);
			await mRepository.SaveAsync(cancellationToken);
		}

		/// <summary>
		/// Blanks the registration token FCM has just rejected, so this session stops being attempted on
		/// every future notification. Without it a device whose app was uninstalled is retried forever,
		/// costing a round-trip and a junk fcm_sent row each time, and the user looks like a delivery
		/// failure rather than someone with no active device.
		///
		/// Note this is exs.fcm-sender writing to user_session, which server-side-base-api-server owns -
		/// the only place this repo does so, and a deliberate exception rather than a precedent.
		///
		/// Only the token is cleared, never StatusId: the session is still a perfectly valid session, and
		/// if it backs authentication then deactivating it would sign someone out for having reinstalled
		/// their app. An empty token already drops the session from the active-session filter above, so
		/// blanking it is all that is needed.
		/// </summary>
		private async Task clearRejectedTokenAsync(UserSession session, CancellationToken cancellationToken)
		{
			var rejectedToken = session.ClientInfo.FCM_FID;
			try
			{
				// Matching on the token as well as the id, not just the id, is what makes this safe to run
				// concurrently: queue rows for the same user are processed in parallel scopes, and the
				// client may have registered a fresh token between the read at the top of this method and
				// this write. Either way the update simply matches nothing rather than discarding a token
				// that is still good.
				var cleared = await mRepository.GetQueryable<UserSession>(s => s.Id == session.Id && s.ClientInfo.FCM_FID == rejectedToken)
					.ExecuteUpdateAsync(s => s.SetProperty(u => u.ClientInfo.FCM_FID, string.Empty), cancellationToken);

				if (cleared > 0)
				{
					mLogger.LogInformation("Cleared the FCM token on session #{SessionId} (user #{UserId}): FCM rejected it as permanently dead.", session.Id, session.UserId);
				}
				else
				{
					mLogger.LogInformation("Session #{SessionId} no longer carries the rejected token - left alone.", session.Id);
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// Housekeeping, not delivery. Letting this escape would fail the whole queue row, which
				// then retries on every poll and re-sends to every session that already succeeded.
				mLogger.LogWarning(ex, "Could not clear the rejected FCM token on session #{SessionId}.", session.Id);
			}
		}

		private readonly IRepository mRepository;
		private readonly ResilientFcmSender mSender;
		private readonly ILogger<NotificationQueueProcessor> mLogger;
	}
}
