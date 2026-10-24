using exs.Database.Commons.Interfaces;
using exs.notifications_contracts;
using exs.notifications_model.Outbox;
using exs.notifications_service.Interfaces;
using Microsoft.Extensions.Options;

namespace exs.notifications_service.Sender
{
	/// <summary>How an email becomes an outbox row, for the fan-out of a notification and for a free-form email alike.</summary>
	internal static class EmailOutbox
	{
		/// <summary>
		/// A Pending email row with its envelope already built: the message id is generated here, once, and the envelope
		/// is what the relay will POST. Nothing is added to a repository.
		/// </summary>
		/// <param name="notificationId">Null for a free-form email.</param>
		/// <param name="userId">Null for a free-form email.</param>
		public static OutboxMessage PendingRow(int? notificationId, int? userId, string? account, string? @ref, EmailBody body, DateTime now, DateTime expires)
		{
			var messageId = Guid.NewGuid();
			var envelope = new MessageEnvelope<EmailBody>
			{
				SchemaVersion = ContractVersion.Current,
				MessageId = messageId,
				Channel = Channels.Email,
				Account = account,
				Ref = @ref,
				CreatedAt = new DateTimeOffset(now, TimeSpan.Zero),
				ExpiresAt = new DateTimeOffset(expires, TimeSpan.Zero),
				Body = body,
			};

			return new OutboxMessage
			{
				MessageId = messageId,
				NotificationId = notificationId,
				UserId = userId,
				Channel = Channels.Email,
				Account = account,
				Ref = @ref,
				Envelope = ContractJson.Serialize(envelope),
				CreatedAt = now,
				ExpiresAt = expires,
				State = OutboxState.Pending,
			};
		}
	}

	/// <summary>
	/// Writes a free-form email (INotificationSender.CreateEmail) as one outbox row. Registered only when
	/// NotificationService:EmailTransport is "Sender", so its absence is how NotificationSender knows email is off.
	/// No notification row is written, on purpose: the notification table is an append-only log that is never pruned,
	/// while an outbox envelope (the address and the text, which for a password reset include a link) is cleared as soon
	/// as the email sender has accepted it.
	/// </summary>
	public sealed class EmailOutboxWriter
	{
		private readonly IOptionsMonitor<SenderTransportOptions> mOptions;
		private readonly TimeProvider mClock;

		internal EmailOutboxWriter(IOptionsMonitor<SenderTransportOptions> options, TimeProvider clock)
		{
			mOptions = options;
			mClock = clock;
		}

		/// <summary>Checks the message and adds its outbox row to the repository. Throws ArgumentException when the message is not valid.</summary>
		internal OutboxMessage Write(IRepository repository, EmailMessage message)
		{
			EmailMessageValidator.Validate(message);

			var options = mOptions.Get(Channels.Email);
			var now = mClock.GetUtcNow().UtcDateTime;
			var body = new EmailBody
			{
				To = new EmailAddress { Address = message.To, Name = message.ToName },
				ReplyTo = message.ReplyTo == null ? null : new EmailAddress { Address = message.ReplyTo, Name = message.ReplyToName },
				Subject = message.Subject,
				Text = message.Text,
				Html = message.Html,
				Priority = message.Priority,
			};

			// The same account rule as the fan-out: the message's own, else this host's default, else none - and then the
			// email sender picks this host's default account itself.
			var row = EmailOutbox.PendingRow(null, null, message.Account ?? options.DefaultAccount, message.Ref, body, now, now.AddMinutes(options.MessageExpirationMinutes));
			repository.Create(row);
			return row;
		}
	}

	/// <summary>
	/// What CreateEmail refuses. A message that would only fail at the sender, or worse be sent to the wrong place, is
	/// stopped here instead, while the caller can still roll back. The exceptions say which field is wrong and never what
	/// it holds: an address or a reset link has no business in an exception message or a log.
	/// </summary>
	internal static class EmailMessageValidator
	{
		public const int MAX_REF_LENGTH = 256;

		/// <summary>The length of notification_outbox."Account".</summary>
		public const int MAX_ACCOUNT_LENGTH = 64;

		public static void Validate(EmailMessage message)
		{
			if (message == null) throw new ArgumentNullException(nameof(message));

			checkAddress(message.To, nameof(EmailMessage.To));
			checkSingleLine(message.ToName, nameof(EmailMessage.ToName));

			if (string.IsNullOrWhiteSpace(message.Subject)) throw invalid(nameof(EmailMessage.Subject), "is required");
			checkSingleLine(message.Subject, nameof(EmailMessage.Subject));

			if (string.IsNullOrWhiteSpace(message.Text) && string.IsNullOrWhiteSpace(message.Html))
			{
				throw invalid($"{nameof(EmailMessage.Text)} and {nameof(EmailMessage.Html)}", "are both empty; at least one is required");
			}

			if (message.ReplyTo != null) checkAddress(message.ReplyTo, nameof(EmailMessage.ReplyTo));
			checkSingleLine(message.ReplyToName, nameof(EmailMessage.ReplyToName));
			if (message.ReplyTo == null && message.ReplyToName != null) throw invalid(nameof(EmailMessage.ReplyToName), $"needs {nameof(EmailMessage.ReplyTo)}");

			if (message.Priority != null && message.Priority != MessagePriorities.Normal && message.Priority != MessagePriorities.High)
			{
				throw invalid(nameof(EmailMessage.Priority), $"must be null, '{MessagePriorities.Normal}' or '{MessagePriorities.High}'");
			}

			if (message.Account != null)
			{
				if (string.IsNullOrWhiteSpace(message.Account)) throw invalid(nameof(EmailMessage.Account), "must not be blank; use null for the default account");
				if (message.Account.Length > MAX_ACCOUNT_LENGTH) throw invalid(nameof(EmailMessage.Account), $"must be at most {MAX_ACCOUNT_LENGTH} characters");
			}

			if (message.Ref != null && message.Ref.Length > MAX_REF_LENGTH) throw invalid(nameof(EmailMessage.Ref), $"must be at most {MAX_REF_LENGTH} characters");
		}

		/// <summary>One bare address: exactly one '@' with something on both sides, and nothing that could make it two addresses or add a header.</summary>
		private static void checkAddress(string? address, string field)
		{
			if (string.IsNullOrWhiteSpace(address)) throw invalid(field, "is required");

			var at = address.IndexOf('@');
			var singleAt = at > 0 && at < address.Length - 1 && address.IndexOf('@', at + 1) < 0;
			if (!singleAt || address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '<' || c == '>' || c == ','))
			{
				throw invalid(field, "must be a single bare email address (no name, angle brackets, spaces or list)");
			}
		}

		private static void checkSingleLine(string? value, string field)
		{
			if (value != null && (value.Contains('\r') || value.Contains('\n'))) throw invalid(field, "must not contain line breaks");
		}

		private static ArgumentException invalid(string field, string problem) =>
			new($"EmailMessage.{field} {problem}.", "message");
	}
}
