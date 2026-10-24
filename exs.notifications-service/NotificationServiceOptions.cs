namespace exs.notifications_service
{
	/// <summary>The values NotificationService:FcmTransport can take.</summary>
	public static class FcmTransportModes
	{
		/// <summary>
		/// The original pipeline: one fcm_queue row per user, drained by the exs.fcm-sender worker that shares
		/// this database. The default, so a host that changes nothing behaves exactly as before.
		/// </summary>
		public const string Queue = "Queue";

		/// <summary>
		/// Pushes go through a standalone sender over HTTP: an outbox row per device, a relay that submits
		/// them, and a puller that reads back the results. Needs NotificationService:FcmSender and the host's hooks.
		/// </summary>
		public const string Sender = "Sender";
	}

	/// <summary>The values NotificationService:EmailTransport can take.</summary>
	public static class EmailTransportModes
	{
		/// <summary>No email. The default, so a host that changes nothing sends none.</summary>
		public const string Off = "Off";

		/// <summary>
		/// Notifications created by CreateEmailNotification, and free-form mails from CreateEmail, are sent through a
		/// standalone email sender over HTTP. Needs NotificationService:EmailSender and the host's email hooks.
		/// </summary>
		public const string Sender = "Sender";
	}

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
		/// How FCM pushes leave this host: "Queue" (the default) or "Sender". Read when the services are
		/// registered, so changing it takes a restart. Switching a host back is changing this one value.
		/// </summary>
		public string FcmTransport { get; set; } = FcmTransportModes.Queue;

		/// <summary>
		/// Whether email can be sent at all: "Off" (the default) or "Sender". Independent of FcmTransport.
		/// Read when the services are registered, so changing it takes a restart.
		/// </summary>
		public string EmailTransport { get; set; } = EmailTransportModes.Off;

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
