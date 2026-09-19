namespace exs.fcm_sender.Fcm
{
	/// <summary>A single, non-retrying attempt to deliver one FCM push message.</summary>
	public interface IFcmMessageSender
	{
		Task<FcmSendResult> SendAsync(string token, short platformId, string title, string body, short typeId, int? entityId, string payload, CancellationToken cancellationToken);
	}
}
