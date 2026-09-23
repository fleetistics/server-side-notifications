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

		/// <summary>
		/// How long an fcm_queue row may sit undelivered before it is dropped and recorded as Expired.
		///
		/// This is the second half of the pipeline's delivery budget, and it runs on its own clock: the
		/// queue row is stamped when the fan-out creates it, not when the notification was created, so
		/// the real worst case from creation to giving up is this plus
		/// NotificationServiceOptions.ExpirationMinutes over in the api-server process. Worth keeping the
		/// two in step - they are one setting split across two services.
		/// </summary>
		public int QueueExpirationMinutes { get; set; } = 30;

		/// <summary>Total send attempts (initial + retries) per session before giving up, in-memory, within one pass.</summary>
		public int MaxAttemptsPerSession { get; set; } = 4;

		/// <summary>Base delay for the exponential backoff between retry attempts.</summary>
		public int RetryBaseDelayMilliseconds { get; set; } = 500;

		/// <summary>Path to the Firebase service-account JSON credentials file.</summary>
		public string FirebaseCredentialsFile { get; set; } = string.Empty;
	}
}
