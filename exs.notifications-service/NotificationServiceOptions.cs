namespace exs.notifications_service
{
	/// <summary>
	/// Tuning for the fan-out stage. Mirrors FcmSenderOptions on the delivery side, and is bound from
	/// the same shape of configuration - but from the host that runs NotificationService (the API
	/// server), which is a different process with its own config files.
	/// </summary>
	public sealed class NotificationServiceOptions
	{
		/// <summary>Configuration section these are bound from.</summary>
		public const string SECTION = "NotificationService";

		/// <summary>
		/// How stale a notification may be before the sweep gives up and marks it Expired instead of
		/// fanning it out. Measured from Notification.Date.
		///
		/// Note this is only half the pipeline's delivery budget: once fanned out, the queue row gets its
		/// own clock and its own expiry (FcmSenderOptions.QueueExpirationMinutes) in the sender process,
		/// so the real worst case from creation to giving up is the sum of the two.
		/// </summary>
		public int ExpirationMinutes { get; set; } = 30;

		/// <summary>
		/// How often the reader loop reconciles against the notification table, picking up anything the
		/// in-memory queue did not deliver. Lower means a dropped notification is recovered sooner;
		/// higher means fewer queries against a table that only rarely has unprocessed rows.
		/// </summary>
		public int SweepIntervalSeconds { get; set; } = 300;

		/// <summary>
		/// How recent a notification has to be for the sweep to leave it to the in-memory queue rather
		/// than fanning it out itself. Needs to comfortably exceed the gap between a row committing and
		/// the reader draining it (milliseconds in practice); too low and a notification can be sent
		/// twice, too high and a genuinely stuck queue takes longer to recover.
		/// </summary>
		public int SweepGracePeriodSeconds { get; set; } = 30;
	}
}
