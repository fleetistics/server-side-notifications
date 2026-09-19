using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace exs.fcm_sender.Fcm
{
	/// <summary>
	/// Adds in-memory retry/backoff around IFcmMessageSender for a single session's send: transient
	/// failures are retried a bounded number of times within this one pass (no DB round-trip between
	/// attempts, nothing persisted until the outcome is final); permanent failures fail immediately.
	/// The pipeline is stateless and safe to reuse concurrently across sessions/queue items.
	/// </summary>
	public sealed class ResilientFcmSender
	{
		public ResilientFcmSender(IFcmMessageSender sender, IOptions<FcmSenderOptions> options)
		{
			mSender = sender;

			var senderOptions = options.Value;
			var maxRetryAttempts = Math.Max(0, senderOptions.MaxAttemptsPerSession - 1);
			// Polly's RetryStrategyOptions rejects MaxRetryAttempts = 0 outright (throws at pipeline
			// build time, i.e. at DI startup) - MaxAttemptsPerSession <= 1 means "no retries", which
			// is just the empty/pass-through pipeline, not a zero-retry AddRetry() call.
			mPipeline = maxRetryAttempts == 0
				? ResiliencePipeline<FcmSendResult>.Empty
				: new ResiliencePipelineBuilder<FcmSendResult>()
					.AddRetry(new RetryStrategyOptions<FcmSendResult>
					{
						ShouldHandle = new PredicateBuilder<FcmSendResult>()
							.HandleResult(r => r.Outcome == FcmSendOutcome.TransientError),
						MaxRetryAttempts = maxRetryAttempts,
						BackoffType = DelayBackoffType.Exponential,
						Delay = TimeSpan.FromMilliseconds(senderOptions.RetryBaseDelayMilliseconds),
					})
					.Build();
		}

		public async Task<(FcmSendResult Result, int Attempts)> SendWithRetryAsync(string token, short platformId, string title, string body, short typeId, int? entityId, string payload, CancellationToken cancellationToken)
		{
			var attempts = 0;
			var result = await mPipeline.ExecuteAsync(async ct =>
			{
				attempts++;
				return await mSender.SendAsync(token, platformId, title, body, typeId, entityId, payload, ct);
			}, cancellationToken);

			return (result, attempts);
		}

		private readonly IFcmMessageSender mSender;
		private readonly ResiliencePipeline<FcmSendResult> mPipeline;
	}
}
