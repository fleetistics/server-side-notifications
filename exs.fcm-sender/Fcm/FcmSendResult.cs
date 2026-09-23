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

	/// <param name="TokenRejected">
	/// FCM has said this specific registration token will never work again, so the session holding it
	/// should stop being sent to. Deliberately narrower than PermanentError, which also covers a
	/// malformed message and a broken APNs credential - neither of which says anything about the token,
	/// and acting on those would deactivate push for every recipient the moment we ship a bad payload
	/// or let a certificate lapse. Set by FcmMessageSender.IsTokenRejected.
	/// </param>
	public sealed record FcmSendResult(FcmSendOutcome Outcome, string? ErrorCode, string? ProviderMessageId, string? ErrorMessage, bool TokenRejected = false)
	{
		public static FcmSendResult Ok(string providerMessageId) =>
			new(FcmSendOutcome.Ok, ErrorCode: null, providerMessageId, ErrorMessage: null);

		public static FcmSendResult Failed(FcmSendOutcome outcome, string? errorCode, string? errorMessage, bool tokenRejected = false) =>
			new(outcome, errorCode, ProviderMessageId: null, errorMessage, tokenRejected);
	}
}
