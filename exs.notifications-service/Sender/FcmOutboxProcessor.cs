using exs.Database.Commons.Interfaces;
using exs.notifications_contracts;
using exs.notifications_model.Notifications;
using exs.notifications_model.Outbox;
using exs.notifications_model.Transports;
using exs.notifications_service.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace exs.notifications_service.Sender
{
	/// <summary>
	/// The FCM transport processor for hosts that use a standalone sender. Where the legacy processor queues
	/// one row per user for a worker that shares the database, this one resolves each user to their devices
	/// and writes one outbox message per device; the relay then hands those to the sender.
	///
	/// Like every processor it only adds rows to the repository it is given. NotificationService saves them,
	/// so the messages commit together and a notification is never marked processed without them.
	/// </summary>
	public sealed class FcmOutboxProcessor : ITransportNotificationProcessor
	{
		private readonly IServiceProvider mServices;
		private readonly SenderTransportOptions mOptions;
		private readonly TimeProvider mClock;
		private readonly ILogger<FcmOutboxProcessor> mLogger;

		// The resolver is looked up when first needed rather than injected; see FcmSenderHooks.
		public FcmOutboxProcessor(IServiceProvider services, IOptions<SenderTransportOptions> options, TimeProvider clock, ILogger<FcmOutboxProcessor> logger)
		{
			mServices = services;
			mOptions = options.Value;
			mClock = clock;
			mLogger = logger;
		}

		public byte TransportType => NotificationTransportType.FCM;

		public async Task ProcessNotificationAsync(IRepository repository, int notificationId, List<int> usersIds, CancellationToken cancellationToken)
		{
			var notification = await repository.GetByIdAsync<Notification>(notificationId, cancellationToken)
				?? throw new InvalidOperationException($"Notification #{notificationId} does not exist.");

			// Idempotent: if an earlier pass already wrote this notification's messages for a user and then
			// failed before marking the notification processed, the sweep comes round again. Skipping those
			// users is what stops that turning into a second push to every device. Only this channel's rows
			// count: the notification's emails, written by the email processor, say nothing about its pushes.
			var alreadyDone = await repository.GetQueryable<OutboxMessage>(o => o.NotificationId == notificationId && o.Channel == Channels.Fcm)
				.Select(o => o.UserId)
				.Distinct()
				.ToListAsync(cancellationToken);
			var users = usersIds.Distinct().Where(u => !alreadyDone.Contains(u)).ToList();
			if (users.Count == 0) return;

			var destinations = await FcmSenderHooks.Require<IFcmDestinationResolver>(mServices).ResolveAsync(repository, notification, users, cancellationToken);

			var now = mClock.GetUtcNow().UtcDateTime;
			var expires = now.AddMinutes(mOptions.MessageExpirationMinutes);

			// Built up first and only added to the repository at the end. NotificationService shares one
			// repository across a whole batch of notifications, so rows added before a failure would be saved
			// along with the next notification's.
			var rows = new List<OutboxMessage>();

			foreach (var destination in destinations)
			{
				var messageId = Guid.NewGuid();
				var envelope = new MessageEnvelope<FcmBody>
				{
					SchemaVersion = ContractVersion.Current,
					MessageId = messageId,
					Channel = Channels.Fcm,
					Account = destination.Account,
					Ref = destination.Ref,
					CreatedAt = new DateTimeOffset(now, TimeSpan.Zero),
					ExpiresAt = new DateTimeOffset(expires, TimeSpan.Zero),
					Body = FcmBodyFactory.Build(notification, destination, mLogger),
				};

				rows.Add(new OutboxMessage
				{
					MessageId = messageId,
					NotificationId = notificationId,
					UserId = destination.UserId,
					Channel = Channels.Fcm,
					Account = destination.Account,
					Ref = destination.Ref,
					Envelope = ContractJson.Serialize(envelope),
					CreatedAt = now,
					ExpiresAt = expires,
					State = OutboxState.Pending,
				});
			}

			// A user with nowhere to send to still gets a row, already finished, so the delivery log answers
			// "what happened for this user" the same way for everyone. The old pipeline recorded this too.
			foreach (var userId in users.Except(destinations.Select(d => d.UserId)))
			{
				mLogger.LogInformation("No FCM destination for user #{UserId} (notification #{NotificationId}); recording that nothing was attempted.", userId, notificationId);
				rows.Add(new OutboxMessage
				{
					MessageId = Guid.NewGuid(),
					NotificationId = notificationId,
					UserId = userId,
					Channel = Channels.Fcm,
					CreatedAt = now,
					ExpiresAt = now,
					State = OutboxState.Completed,
					ResultStatus = ResultStatuses.Failed,
					ErrorKind = OutboxErrorKind.NoDestination,
					ErrorCode = FcmSentErrorCode.NoActiveSessions,
					CompletedAt = now,
				});
			}

			foreach (var row in rows)
			{
				repository.Create(row);
			}
		}
	}
}
