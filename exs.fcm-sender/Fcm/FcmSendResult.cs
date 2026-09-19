namespace exs.fcm_sender.Fcm
{
	public enum FcmSendOutcome
	{
		Ok,

		/// <summary>Worth retrying: rate limiting, transient backend/network trouble.</summary>
		TransientError,

		/// <summary>Not worth retrying: dead/invalid token, malformed request, auth misconfiguration.</summary>
		PermanentError,
	}

	public sealed record FcmSendResult(FcmSendOutcome Outcome, string? ErrorCode, string? ProviderMessageId, string? ErrorMessage)
	{
		public static FcmSendResult Ok(string providerMessageId) =>
			new(FcmSendOutcome.Ok, ErrorCode: null, providerMessageId, ErrorMessage: null);

		public static FcmSendResult Failed(FcmSendOutcome outcome, string? errorCode, string? errorMessage) =>
			new(outcome, errorCode, ProviderMessageId: null, errorMessage);
	}
}
