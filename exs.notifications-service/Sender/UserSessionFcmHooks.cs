using exs.Database.Commons.Interfaces;
using exs.modelCommons.AppStructure;
using exs.modelCommons.UserManagement;
using exs.notifications_contracts;
using exs.notifications_model.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace exs.notifications_service.Sender
{
	/// <summary>
	/// What a destination's Ref holds for the user-session hooks: the session that was resolved. The sender
	/// never reads it; it comes back with the result for diagnostics.
	/// </summary>
	public static class FcmRef
	{
		public static string ForSession(int sessionId) => $"s:{sessionId}";
	}

	/// <summary>
	/// For hosts that use the estate's user_session table: a user's destinations are the tokens of their
	/// active iOS and Android sessions. The same rule the old queue worker applied, moved to the host's side
	/// because the sender no longer has access to user_session.
	/// </summary>
	public sealed class UserSessionFcmDestinationResolver : IFcmDestinationResolver
	{
		private readonly SenderTransportOptions mOptions;

		public UserSessionFcmDestinationResolver(IOptions<SenderTransportOptions> options)
		{
			mOptions = options.Value;
		}

		public async Task<IReadOnlyList<FcmResolvedDestination>> ResolveAsync(IRepository repository, Notification notification, IReadOnlyCollection<int> userIds, CancellationToken cancellationToken)
		{
			if (userIds.Count == 0) return [];

			var ids = userIds.ToList();
			// Only the plain columns are filtered in the query. Platform and token sit inside the owned
			// ClientInfo, and a user has a handful of sessions at most, so filtering those in memory costs
			// nothing and keeps the query independent of the provider's owned-type translation.
			var candidates = await repository.GetQueryable<UserSession>(s => ids.Contains(s.UserId) && s.StatusId == UserSessionStatus.Active)
				.AsNoTracking()
				.ToListAsync(cancellationToken);

			return candidates
				.Where(s => (s.ClientInfo.PlatformId == ClientDevicePlatform.Ios || s.ClientInfo.PlatformId == ClientDevicePlatform.Android) &&
					!string.IsNullOrEmpty(s.ClientInfo.FCM_FID))
				// The same device can appear in two sessions of one user (logging in again without the old
				// session having closed). One push per token, not two: keep the newest session.
				.GroupBy(s => (s.UserId, Token: s.ClientInfo.FCM_FID))
				.Select(g => g.OrderByDescending(s => s.Id).First())
				.OrderBy(s => s.UserId).ThenBy(s => s.Id)
				.Select(s => new FcmResolvedDestination(
					s.UserId,
					s.ClientInfo.FCM_FID,
					s.ClientInfo.PlatformId == ClientDevicePlatform.Ios ? FcmPlatforms.Ios : FcmPlatforms.Android,
					accountFor(s),
					FcmRef.ForSession(s.Id)))
				.ToList();
		}

		private string? accountFor(UserSession session) =>
			mOptions.AccountsByAppId.TryGetValue(session.ClientInfo.AppUID, out var account) ? account : mOptions.DefaultAccount;
	}

	/// <summary>
	/// Clears a dead token from the user's sessions. Matched on the token itself rather than on the session
	/// the result names, for two reasons: every session of the user that carries a dead token is dead, not
	/// just the newest; and matching on the value is what makes this safe to run late, since a token the
	/// client has registered afresh in the meantime no longer matches and is left alone.
	///
	/// Only the token is cleared, never the session's status: the session is still a valid login, and
	/// deactivating it would sign someone out for having reinstalled the app. An empty token already drops
	/// the session from the resolver, which is all that is needed.
	/// </summary>
	public sealed class UserSessionFcmTokenInvalidator : IFcmTokenInvalidator
	{
		private readonly ILogger<UserSessionFcmTokenInvalidator> mLogger;

		public UserSessionFcmTokenInvalidator(ILogger<UserSessionFcmTokenInvalidator> logger)
		{
			mLogger = logger;
		}

		public async Task<bool> InvalidateAsync(IRepository repository, int userId, string? @ref, string destination, CancellationToken cancellationToken)
		{
			if (string.IsNullOrEmpty(destination)) return false;

			var cleared = await repository.GetQueryable<UserSession>(s => s.UserId == userId && s.ClientInfo.FCM_FID == destination)
				.ExecuteUpdateAsync(s => s.SetProperty(u => u.ClientInfo.FCM_FID, string.Empty), cancellationToken);

			if (cleared > 0)
			{
				mLogger.LogInformation("Cleared a dead FCM token on {Count} session(s) of user #{UserId} (result ref {Ref}).", cleared, userId, @ref);
			}
			return cleared > 0;
		}
	}
}
