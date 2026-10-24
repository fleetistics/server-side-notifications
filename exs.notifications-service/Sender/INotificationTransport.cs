using exs.notifications_contracts;

namespace exs.notifications_service.Sender
{
	public enum SubmitOutcome
	{
		/// <summary>The sender stored the message and will send it.</summary>
		Accepted,

		/// <summary>The sender already had this message (a resubmission after a lost answer). Same as Accepted.</summary>
		Duplicate,

		/// <summary>The sender refused the message for good. Retrying the same message cannot change that.</summary>
		Rejected,

		/// <summary>
		/// The message did not get through for a reason that is not about the message: the sender is down or slow,
		/// or this host's own API key is wrong. Retry later; do not give up on the message.
		/// </summary>
		TransientFailure,
	}

	public sealed record SubmitResult(SubmitOutcome Outcome, string? Detail = null);

	/// <summary>
	/// How the host reaches a standalone sender. HTTP is the implementation today; a RabbitMQ one would sit
	/// behind the same two calls, so the outbox, the relay and the result handling would not change.
	/// </summary>
	public interface INotificationTransport
	{
		/// <summary>Hands one serialized envelope to the sender.</summary>
		Task<SubmitResult> SubmitAsync(string envelopeJson, CancellationToken cancellationToken);

		/// <summary>Reads the next page of this host's results, after the given cursor position.</summary>
		Task<ResultPage> FetchResultsAsync(long after, int limit, CancellationToken cancellationToken);
	}
}
