namespace exs.fcm_sender
{
	public sealed class FcmSenderOptions
	{
		/// <summary>How long to wait before polling again when the last batch found nothing.</summary>
		public int PollIntervalSeconds { get; set; } = 5;

		/// <summary>Max NotificationQueue rows (TransportType = FCM) claimed per poll.</summary>
		public int BatchSize { get; set; } = 200;

		/// <summary>Max queue rows processed concurrently within a batch.</summary>
		public int MaxConcurrency { get; set; } = 8;

		/// <summary>Total send attempts (initial + retries) per session before giving up, in-memory, within one pass.</summary>
		public int MaxAttemptsPerSession { get; set; } = 4;

		/// <summary>Base delay for the exponential backoff between retry attempts.</summary>
		public int RetryBaseDelayMilliseconds { get; set; } = 500;

		/// <summary>Path to the Firebase service-account JSON credentials file.</summary>
		public string FirebaseCredentialsFile { get; set; } = string.Empty;
	}
}
