using exs.fcm_sender.Fcm;

namespace exs.fcm_sender.Tests
{
	/// <summary>
	/// Scripted IFcmMessageSender: returns the next result from Results on each call (repeating the
	/// last one once exhausted) and records every call for assertions.
	/// </summary>
	public sealed class StubFcmMessageSender : IFcmMessageSender
	{
		public StubFcmMessageSender(params FcmSendResult[] results)
		{
			mResults = results;
		}

		public List<(string Token, short PlatformId, string Title, string Body, short TypeId, int? EntityId, string Payload)> Calls { get; } = [];

		public Task<FcmSendResult> SendAsync(string token, short platformId, string title, string body, short typeId, int? entityId, string payload, CancellationToken cancellationToken)
		{
			Calls.Add((token, platformId, title, body, typeId, entityId, payload));
			var result = mResults.Length == 0
				? FcmSendResult.Ok("stub-message")
				: mResults[Math.Min(Calls.Count - 1, mResults.Length - 1)];
			return Task.FromResult(result);
		}

		private readonly FcmSendResult[] mResults;
	}
}
