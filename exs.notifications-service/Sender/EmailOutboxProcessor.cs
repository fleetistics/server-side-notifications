using exs.Database.Commons.Interfaces;
using exs.notifications_contracts;
using exs.notifications_model.Notifications;
using exs.notifications_model.Outbox;
using exs.notifications_service.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace exs.notifications_service.Sender
{
	/// <summary>
	/// The email transport processor: for a notification created by CreateEmailNotification, resolves each user to an
	/// address and writes one outbox message per address; the email relay then hands those to the email sender.
	/// NotificationService only gives it notifications whose TransportType is Email, so a push notification is never mailed.
	///
	/// Works exactly like FcmOutboxProcessor, and next to it: the same outbox table, told apart by Channel. Like
	/// every processor it only adds rows to the repository it is given. NotificationService saves them, so the
	/// messages commit together and a notification is never marked processed without them.
	/// </summary>
	public sealed class EmailOutboxProcessor : ITransportNotificationProcessor
	{
		private readonly IServiceProvider mServices;
		private readonly SenderTransportOptions mOptions;
		private readonly TimeProvider mClock;
		private readonly ILogger<EmailOutboxProcessor> mLogger;

		// The resolver is looked up when first needed rather than injected; see EmailSenderHooks.
		public EmailOutboxProcessor(IServiceProvider services, IOptions<SenderTransportOptions> options, TimeProvider clock, ILogger<EmailOutboxProcessor> logger)
		{
			mServices = services;
			mOptions = options.Value;
			mClock = clock;
			mLogger = logger;
		}

		public byte TransportType => NotificationTransportType.Email;

		public async Task ProcessNotificationAsync(IRepository repository, int notificationId, List<int> usersIds, CancellationToken cancellationToken)
		{
			var notification = await repository.GetByIdAsync<Notification>(notificationId, cancellationToken)
				?? throw new InvalidOperationException($"Notification #{notificationId} does not exist.");

			// Idempotent per channel: if an earlier pass already wrote this notification's emails for a user and
			// then failed before marking the notification processed, the sweep comes round again. Skipping those
			// users is what stops that turning into a second email. Channel matters: the same notification's push
			// rows must not count, or an email would never be written for a user who was pushed first.
			var alreadyDone = await repository.GetQueryable<OutboxMessage>(o => o.NotificationId == notificationId && o.Channel == Channels.Email)
				.Select(o => o.UserId)
				.Distinct()
				.ToListAsync(cancellationToken);
			var users = usersIds.Distinct().Where(u => !alreadyDone.Contains(u)).ToList();
			if (users.Count == 0) return;

			var destinations = await EmailSenderHooks.Require<IEmailDestinationResolver>(mServices).ResolveAsync(repository, notification, users, cancellationToken);
			var contentFactory = mServices.GetService<IEmailContentFactory>() ?? new DefaultEmailContentFactory();

			var now = mClock.GetUtcNow().UtcDateTime;
			var expires = now.AddMinutes(mOptions.MessageExpirationMinutes);

			// Built up first and only added to the repository at the end. NotificationService shares one
			// repository across a whole batch of notifications, so rows added before a failure would be saved
			// along with the next notification's.
			var rows = new List<OutboxMessage>();

			foreach (var destination in destinations)
			{
				var content = contentFactory.Build(notification, destination);
				if (content == null)
				{
					mLogger.LogInformation("Notification #{NotificationId} has no email form for user #{UserId}; recording that nothing was attempted.", notificationId, destination.UserId);
					rows.Add(finishedRow(notificationId, destination.UserId, now, OutboxErrorKind.NoContent, OutboxErrorCode.NoEmailContent, destination.Ref));
					continue;
				}

				var body = new EmailBody
				{
					To = new EmailAddress { Address = destination.Address, Name = destination.Name },
					ReplyTo = content.ReplyTo == null ? null : new EmailAddress { Address = content.ReplyTo },
					Subject = content.Subject,
					Text = content.Text,
					Html = content.Html,
					Priority = content.Priority,
				};
				rows.Add(EmailOutbox.PendingRow(notificationId, destination.UserId, destination.Account ?? mOptions.DefaultAccount, destination.Ref, body, now, expires));
			}

			// A user with no address still gets a row, already finished, so the delivery log answers "what
			// happened for this user" the same way for everyone.
			foreach (var userId in users.Except(destinations.Select(d => d.UserId)))
			{
				mLogger.LogInformation("No email address for user #{UserId} (notification #{NotificationId}); recording that nothing was attempted.", userId, notificationId);
				rows.Add(finishedRow(notificationId, userId, now, OutboxErrorKind.NoDestination, OutboxErrorCode.NoEmailAddress, @ref: null));
			}

			foreach (var row in rows)
			{
				repository.Create(row);
			}
		}

		private static OutboxMessage finishedRow(int notificationId, int userId, DateTime now, string errorKind, string errorCode, string? @ref) => new()
		{
			MessageId = Guid.NewGuid(),
			NotificationId = notificationId,
			UserId = userId,
			Channel = Channels.Email,
			Ref = @ref,
			CreatedAt = now,
			ExpiresAt = now,
			State = OutboxState.Completed,
			ResultStatus = ResultStatuses.Failed,
			ErrorKind = errorKind,
			ErrorCode = errorCode,
			CompletedAt = now,
		};
	}
}
