using exs.fcm_sender.Fcm;

namespace exs.notifications_pipeline.E2ETests
{
	/// <summary>Records every call and always reports success - the only thing this suite fakes.</summary>
	public sealed class StubFcmMessageSender : IFcmMessageSender
	{
		public List<(string Token, short PlatformId)> Calls { get; } = [];

		public Task<FcmSendResult> SendAsync(string token, short platformId, string title, string body, short typeId, int? entityId, string payload, CancellationToken cancellationToken)
		{
			Calls.Add((token, platformId));
			return Task.FromResult(FcmSendResult.Ok("stub-message-id"));
		}
	}
}
