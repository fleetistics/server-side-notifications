using Microsoft.Extensions.Options;

namespace exs.notifications_service.Sender
{
	/// <summary>
	/// How this host talks to one standalone sender. There is one set of these per channel, and they are the
	/// same settings for each: the unnamed options are the FCM sender's ("NotificationService:FcmSender", only read
	/// when NotificationService:FcmTransport is "Sender"), and the options named "email" are the email sender's
	/// ("NotificationService:EmailSender", only read when NotificationService:EmailTransport is "Sender").
	/// </summary>
	public sealed class SenderTransportOptions
	{
		public const string FCM_SECTION = "NotificationService:FcmSender";

		public const string EMAIL_SECTION = "NotificationService:EmailSender";

		/// <summary>The configuration section for the options of the given name: "email" for the email sender, anything else is the FCM sender.</summary>
		public static string SectionFor(string? name) => name == exs.notifications_contracts.Channels.Email ? EMAIL_SECTION : FCM_SECTION;

		/// <summary>The sender's root URL, for example https://senders.internal:5100.</summary>
		public string BaseUrl { get; set; } = "";

		/// <summary>The API key this host was given by the sender's operator. The sender stores only its SHA-256.</summary>
		public string ApiKey { get; set; } = "";

		/// <summary>
		/// Per HTTP call. Keep it above the 30-odd seconds a sender whose database is down takes to answer 500,
		/// or this host gives up first and resubmits - harmless, since the sender deduplicates, but noisy.
		/// </summary>
		public int RequestTimeoutSeconds { get; set; } = 60;

		/// <summary>How long the sender has, from the moment of fan-out, to send a message before it counts as expired.</summary>
		public int MessageExpirationMinutes { get; set; } = 30;

		/// <summary>Most outbox rows handed to the submitting workers per poll.</summary>
		public int RelayBatchSize { get; set; } = 100;

		/// <summary>Submissions in flight at once.</summary>
		public int RelayMaxConcurrency { get; set; } = 4;

		/// <summary>
		/// Fallback poll. The fan-out wakes the relay as soon as it has written rows, and a backoff ending
		/// wakes it too, so this only matters for rows written by another process or left over from a restart.
		/// </summary>
		public int RelayPollIntervalSeconds { get; set; } = 5;

		/// <summary>Longest pause after the sender (or one row) keeps failing. It doubles from 2 seconds up to this.</summary>
		public int RelayBackoffMaxSeconds { get; set; } = 60;

		/// <summary>How often the sender's result log is read.</summary>
		public int ResultPollIntervalSeconds { get; set; } = 3;

		public int ResultPageSize { get; set; } = 200;

		/// <summary>How long finished outbox rows (the delivery log) are kept.</summary>
		public int OutboxRetentionDays { get; set; } = 30;

		/// <summary>The account a message is sent as when none is chosen by the app. Null: the sender uses this host's default.</summary>
		public string? DefaultAccount { get; set; }

		/// <summary>
		/// For hosts with more than one mobile app (so more than one Firebase project): which account each app's
		/// tokens belong to, keyed by the app id the session reports (SessionClientInfo.AppUID).
		/// </summary>
		public Dictionary<string, string> AccountsByAppId { get; set; } = new();
	}

	internal sealed class SenderTransportOptionsValidator : IValidateOptions<SenderTransportOptions>
	{
		public ValidateOptionsResult Validate(string? name, SenderTransportOptions options)
		{
			var failures = new List<string>();
			var section = SenderTransportOptions.SectionFor(name);
			void atLeast(string setting, int value, int minimum)
			{
				if (value < minimum) failures.Add($"{section}:{setting} must be at least {minimum}.");
			}

			if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUrl) || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
			{
				failures.Add($"{section}:BaseUrl must be an absolute http(s) URL (it is '{options.BaseUrl}').");
			}
			if (string.IsNullOrWhiteSpace(options.ApiKey))
			{
				failures.Add($"{section}:ApiKey is not set.");
			}

			atLeast(nameof(options.RequestTimeoutSeconds), options.RequestTimeoutSeconds, 1);
			atLeast(nameof(options.MessageExpirationMinutes), options.MessageExpirationMinutes, 1);
			atLeast(nameof(options.RelayBatchSize), options.RelayBatchSize, 1);
			atLeast(nameof(options.RelayMaxConcurrency), options.RelayMaxConcurrency, 1);
			atLeast(nameof(options.RelayPollIntervalSeconds), options.RelayPollIntervalSeconds, 1);
			atLeast(nameof(options.RelayBackoffMaxSeconds), options.RelayBackoffMaxSeconds, 1);
			atLeast(nameof(options.ResultPollIntervalSeconds), options.ResultPollIntervalSeconds, 1);
			atLeast(nameof(options.ResultPageSize), options.ResultPageSize, 1);
			atLeast(nameof(options.OutboxRetentionDays), options.OutboxRetentionDays, 1);

			return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
		}
	}
}
