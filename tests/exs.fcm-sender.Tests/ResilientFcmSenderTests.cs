using exs.fcm_sender;
using exs.fcm_sender.Fcm;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.fcm_sender.Tests
{
	public class ResilientFcmSenderTests
	{
		[Fact]
		public async Task SendWithRetryAsync_OkOnFirstAttempt_DoesNotRetry()
		{
			var stub = new StubFcmMessageSender(FcmSendResult.Ok("msg-1"));
			var sender = createSender(stub, maxAttempts: 4);

			var (result, attempts) = await sender.SendWithRetryAsync("token", 1, "title", "body", 1, null, "", CancellationToken.None);

			result.Outcome.ShouldBe(FcmSendOutcome.Ok);
			attempts.ShouldBe(1);
			stub.Calls.Count.ShouldBe(1);
		}

		[Fact]
		public async Task SendWithRetryAsync_TransientThenOk_RetriesOnceThenSucceeds()
		{
			var stub = new StubFcmMessageSender(
				FcmSendResult.Failed(FcmSendOutcome.TransientError, "UNAVAILABLE", "temporary"),
				FcmSendResult.Ok("msg-2"));
			var sender = createSender(stub, maxAttempts: 4);

			var (result, attempts) = await sender.SendWithRetryAsync("token", 1, "title", "body", 1, null, "", CancellationToken.None);

			result.Outcome.ShouldBe(FcmSendOutcome.Ok);
			attempts.ShouldBe(2);
			stub.Calls.Count.ShouldBe(2);
		}

		[Fact]
		public async Task SendWithRetryAsync_AlwaysTransient_GivesUpAtMaxAttempts()
		{
			var stub = new StubFcmMessageSender(FcmSendResult.Failed(FcmSendOutcome.TransientError, "UNAVAILABLE", "temporary"));
			var sender = createSender(stub, maxAttempts: 3);

			var (result, attempts) = await sender.SendWithRetryAsync("token", 1, "title", "body", 1, null, "", CancellationToken.None);

			result.Outcome.ShouldBe(FcmSendOutcome.TransientError);
			attempts.ShouldBe(3);
			stub.Calls.Count.ShouldBe(3);
		}

		[Fact]
		public async Task SendWithRetryAsync_PermanentError_NeverRetries()
		{
			var stub = new StubFcmMessageSender(FcmSendResult.Failed(FcmSendOutcome.PermanentError, "UNREGISTERED", "dead token"));
			var sender = createSender(stub, maxAttempts: 5);

			var (result, attempts) = await sender.SendWithRetryAsync("token", 1, "title", "body", 1, null, "", CancellationToken.None);

			result.Outcome.ShouldBe(FcmSendOutcome.PermanentError);
			attempts.ShouldBe(1);
			stub.Calls.Count.ShouldBe(1);
		}

		[Fact]
		public void MaxAttemptsPerSessionOfOne_DoesNotThrowAtConstruction()
		{
			// Regression guard: Polly's RetryStrategyOptions rejects MaxRetryAttempts = 0 outright, so
			// "no retries" must build the empty/pass-through pipeline rather than AddRetry(0).
			var stub = new StubFcmMessageSender(FcmSendResult.Ok("msg"));

			Should.NotThrow(() => createSender(stub, maxAttempts: 1));
		}

		[Fact]
		public async Task MaxAttemptsPerSessionOfOne_SendsExactlyOnceOnTransientFailure()
		{
			var stub = new StubFcmMessageSender(FcmSendResult.Failed(FcmSendOutcome.TransientError, "UNAVAILABLE", "temporary"));
			var sender = createSender(stub, maxAttempts: 1);

			var (result, attempts) = await sender.SendWithRetryAsync("token", 1, "title", "body", 1, null, "", CancellationToken.None);

			result.Outcome.ShouldBe(FcmSendOutcome.TransientError);
			attempts.ShouldBe(1);
			stub.Calls.Count.ShouldBe(1);
		}

		private static ResilientFcmSender createSender(StubFcmMessageSender stub, int maxAttempts)
		{
			var options = Options.Create(new FcmSenderOptions
			{
				MaxAttemptsPerSession = maxAttempts,
				RetryBaseDelayMilliseconds = 1, // keep the exponential backoff effectively instant in tests
			});
			return new ResilientFcmSender(stub, options);
		}
	}
}
