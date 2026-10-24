using System.Net;
using System.Text;
using System.Text.Json;
using exs.notifications_contracts;
using Microsoft.Extensions.Logging;

namespace exs.notifications_service.Sender
{
	public sealed class HttpNotificationTransport : INotificationTransport
	{
		/// <summary>The named HttpClient; the base address, API key and timeout are set where it is registered.</summary>
		public const string ClientName = "exs-notification-sender";

		/// <summary>The named HttpClient of the email sender. Each channel has its own: its own address and API key.</summary>
		public const string EmailClientName = "exs-notification-sender-email";

		private readonly IHttpClientFactory mFactory;
		private readonly ILogger<HttpNotificationTransport> mLogger;
		private readonly string mClientName;

		public HttpNotificationTransport(IHttpClientFactory factory, ILogger<HttpNotificationTransport> logger, string clientName = ClientName)
		{
			mFactory = factory;
			mLogger = logger;
			mClientName = clientName;
		}

		public async Task<SubmitResult> SubmitAsync(string envelopeJson, CancellationToken cancellationToken)
		{
			using var client = mFactory.CreateClient(mClientName);
			using var content = new StringContent(envelopeJson, Encoding.UTF8, "application/json");

			HttpResponseMessage response;
			try
			{
				response = await client.PostAsync("v1/messages", content, cancellationToken);
			}
			catch (HttpRequestException ex)
			{
				return new SubmitResult(SubmitOutcome.TransientFailure, $"The sender could not be reached: {ex.Message}");
			}
			catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				return new SubmitResult(SubmitOutcome.TransientFailure, "The sender did not answer in time.");
			}

			using (response)
			{
				var status = response.StatusCode;
				var body = await response.Content.ReadAsStringAsync(cancellationToken);

				if (status == HttpStatusCode.Accepted)
				{
					return new SubmitResult(parseIntakeStatus(body) == IntakeStatuses.Duplicate ? SubmitOutcome.Duplicate : SubmitOutcome.Accepted);
				}

				// 401 says nothing about the message: it is this host's own API key being wrong, which fails every
				// submission at once. Marking each one rejected would burn the whole backlog for a fault that one
				// config change fixes, so it is treated like the sender being down: wait, and keep the messages.
				if (status == HttpStatusCode.Unauthorized)
				{
					mLogger.LogError("The sender answered 401 to a submission: this host's API key is not accepted. Messages stay queued until it is fixed.");
					return new SubmitResult(SubmitOutcome.TransientFailure, "The sender did not accept this host's API key (401).");
				}

				if (status == HttpStatusCode.RequestTimeout || status == HttpStatusCode.TooManyRequests || (int)status >= 500)
				{
					return new SubmitResult(SubmitOutcome.TransientFailure, $"The sender answered {(int)status}.");
				}

				// Any other 4xx is the sender looking at this message and refusing it.
				return new SubmitResult(SubmitOutcome.Rejected, $"The sender refused the message ({(int)status}): {describeErrors(body)}");
			}
		}

		public async Task<ResultPage> FetchResultsAsync(long after, int limit, CancellationToken cancellationToken)
		{
			using var client = mFactory.CreateClient(mClientName);
			using var response = await client.GetAsync($"v1/results?after={after}&limit={limit}", cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				throw new HttpRequestException($"The sender answered {(int)response.StatusCode} to a result request.", null, response.StatusCode);
			}

			var json = await response.Content.ReadAsStringAsync(cancellationToken);
			return ContractJson.Deserialize<ResultPage>(json) ?? throw new InvalidOperationException("The sender returned an empty result page.");
		}

		private static string? parseIntakeStatus(string body)
		{
			try
			{
				return ContractJson.Deserialize<IntakeResponse>(body)?.Status;
			}
			catch (JsonException)
			{
				return null;
			}
		}

		private static string describeErrors(string body)
		{
			try
			{
				var errors = ContractJson.Deserialize<ErrorResponse>(body)?.Errors;
				if (errors is { Count: > 0 })
				{
					return string.Join("; ", errors.Select(e => $"{e.Code}: {e.Message}"));
				}
			}
			catch (JsonException)
			{
			}
			return body.Length > 300 ? body[..300] : body;
		}
	}
}
