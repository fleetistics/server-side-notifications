using exs.Database.Commons.Interfaces;
using exs.notifications_model.Notifications;

namespace exs.notifications_service.Sender
{
	/// <summary>One place a push should go: a device registration token of a particular user.</summary>
	/// <param name="Platform">One of exs.notifications_contracts.FcmPlatforms.</param>
	/// <param name="Account">Which Firebase project the token belongs to. Null: the sender's default for this host.</param>
	/// <param name="Ref">An opaque string handed back with the result, so the host can find what it needs again.</param>
	public sealed record FcmResolvedDestination(int UserId, string Token, string Platform, string? Account = null, string? Ref = null);

	/// <summary>
	/// The part of sending a push that only the host can know: where a user's devices are. A standalone
	/// sender has no users, so before anything is handed over the host turns user ids into destinations.
	///
	/// Registered as a singleton and called concurrently, so an implementation holds no per-request state;
	/// it gets the repository of the fan-out's own scope on every call, like every other service in this estate.
	/// </summary>
	public interface IFcmDestinationResolver
	{
		/// <summary>
		/// Returns the destinations for these users. A user with none simply does not appear in the result;
		/// the caller records that nothing could be attempted for them.
		/// </summary>
		Task<IReadOnlyList<FcmResolvedDestination>> ResolveAsync(IRepository repository, Notification notification, IReadOnlyCollection<int> userIds, CancellationToken cancellationToken);
	}

	/// <summary>
	/// The other part only the host can do: when the sender reports that a token is dead for good, stop using it.
	/// Called after the result has been recorded, and best effort: a failure here is logged, not retried.
	/// </summary>
	public interface IFcmTokenInvalidator
	{
		/// <param name="userId">Whose token it was.</param>
		/// <param name="ref">The Ref the resolver put on the destination.</param>
		/// <param name="destination">The dead token, as the sender echoed it. Clear it only where it still matches, so a token the client has refreshed since is left alone.</param>
		/// <returns>Whether anything was cleared.</returns>
		Task<bool> InvalidateAsync(IRepository repository, int userId, string? @ref, string destination, CancellationToken cancellationToken);
	}
}
