namespace exs.notifications_contracts
{
	// These vocabularies are plain strings on the wire, not enums, on purpose: a value added later must
	// not make an older reader throw while deserializing. Readers treat an unknown value as "something
	// I do not understand yet" and decide what to do with it.

	public static class Channels
	{
		public const string Fcm = "fcm";
		public const string Email = "email";
	}

	public static class FcmPlatforms
	{
		public const string Android = "android";
		public const string Ios = "ios";
	}

	public static class MessagePriorities
	{
		public const string Normal = "normal";
		public const string High = "high";
	}

	public static class ResultStatuses
	{
		/// <summary>
		/// The provider accepted the message. Deliberately not "delivered": FCM only reports that it took the
		/// message, never that a device received it, and for email it means the SMTP server took it for
		/// delivery - a bounce or a spam folder later is not visible here.
		/// </summary>
		public const string Sent = "sent";
		public const string Failed = "failed";
		/// <summary>ExpiresAt passed before the message could be sent.</summary>
		public const string Expired = "expired";
	}

	public static class ErrorKinds
	{
		/// <summary>Retries were used up on errors that might have cleared by themselves.</summary>
		public const string TransientExhausted = "transient_exhausted";
		/// <summary>The provider refused the message and retrying will not change that.</summary>
		public const string Permanent = "permanent";
		/// <summary>The destination itself is gone (see ResultEvent.DestinationInvalid).</summary>
		public const string DestinationInvalid = "destination_invalid";
		/// <summary>The message failed validation, or named an unknown or disallowed account.</summary>
		public const string InvalidMessage = "invalid_message";
	}
}
