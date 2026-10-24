using exs.Database.Commons.Interfaces;
using exs.modelCommons.AppStructure;
using exs.modelCommons.UserManagement;
using exs.notifications_contracts;
using exs.notifications_model.Notifications;
using exs.notifications_service.Sender;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.notifications_service.Tests.Sender
{
	public sealed class UserSessionFcmHooksTests : IAsyncDisposable
	{
		private readonly HostTestDatabase mDatabase = HostTestDatabase.Create();

		public ValueTask DisposeAsync() => mDatabase.DisposeAsync();

		private static readonly Notification AnyNotification = new() { Id = 1 };

		private async Task<int> addSessionAsync(int userId, string token, short platform = ClientDevicePlatform.Android, short status = UserSessionStatus.Active, string appUid = "")
		{
			await using var context = mDatabase.CreateContext();
			var session = new UserSession
			{
				UserId = userId,
				StatusId = status,
				ClientInfo = new SessionClientInfo { FCM_FID = token, PlatformId = platform, AppUID = appUid },
			};
			context.Set<UserSession>().Add(session);
			await context.SaveChangesAsync();
			return session.Id;
		}

		private UserSessionFcmDestinationResolver resolver(Action<SenderTransportOptions>? configure = null)
		{
			var options = new SenderTransportOptions();
			configure?.Invoke(options);
			return new UserSessionFcmDestinationResolver(Options.Create(options));
		}

		private async Task<IReadOnlyList<FcmResolvedDestination>> resolveAsync(UserSessionFcmDestinationResolver subject, params int[] userIds)
		{
			using var repository = mDatabase.CreateRepository();
			return await subject.ResolveAsync(repository, AnyNotification, userIds, CancellationToken.None);
		}

		public class Resolving : IAsyncDisposable
		{
			private readonly UserSessionFcmHooksTests mOuter = new();

			public ValueTask DisposeAsync() => mOuter.DisposeAsync();

			[Fact]
			public async Task ReturnsTheTokensOfActiveIosAndAndroidSessions_WithThePlatformAsAString()
			{
				var android = await mOuter.addSessionAsync(1, "tok-android", ClientDevicePlatform.Android);
				var ios = await mOuter.addSessionAsync(1, "tok-ios", ClientDevicePlatform.Ios);

				var destinations = await mOuter.resolveAsync(mOuter.resolver(), 1);

				destinations.Count.ShouldBe(2);
				var a = destinations.Single(d => d.Token == "tok-android");
				a.UserId.ShouldBe(1);
				a.Platform.ShouldBe(FcmPlatforms.Android);
				a.Ref.ShouldBe($"s:{android}");
				destinations.Single(d => d.Token == "tok-ios").Platform.ShouldBe(FcmPlatforms.Ios);
				destinations.Single(d => d.Token == "tok-ios").Ref.ShouldBe($"s:{ios}");
			}

			[Fact]
			public async Task SkipsWhatCannotReceiveAPush()
			{
				await mOuter.addSessionAsync(1, "tok-loggedout", status: UserSessionStatus.LoggedOutByUser);
				await mOuter.addSessionAsync(1, "tok-expired", status: UserSessionStatus.Expired);
				await mOuter.addSessionAsync(1, "tok-windows", ClientDevicePlatform.Windows);
				await mOuter.addSessionAsync(1, "tok-unknown", ClientDevicePlatform.Unknown);
				await mOuter.addSessionAsync(1, "");                     // no token registered
				await mOuter.addSessionAsync(2, "tok-someone-else");
				var good = await mOuter.addSessionAsync(1, "tok-good");

				var destinations = await mOuter.resolveAsync(mOuter.resolver(), 1);

				destinations.Select(d => d.Token).ShouldBe(["tok-good"]);
				destinations.Single().Ref.ShouldBe($"s:{good}");
			}

			[Fact]
			public async Task ResolvesSeveralUsersInOneCall_InAStableOrder()
			{
				await mOuter.addSessionAsync(2, "tok-2");
				await mOuter.addSessionAsync(1, "tok-1");
				await mOuter.addSessionAsync(3, "tok-3-not-asked-for");

				var destinations = await mOuter.resolveAsync(mOuter.resolver(), 2, 1);

				destinations.Select(d => (d.UserId, d.Token)).ShouldBe([(1, "tok-1"), (2, "tok-2")]);
			}

			[Fact]
			public async Task OneDeviceInTwoSessionsOfTheSameUser_GetsOnePush_FromTheNewestSession()
			{
				await mOuter.addSessionAsync(1, "tok-same");
				var newest = await mOuter.addSessionAsync(1, "tok-same");

				var destinations = await mOuter.resolveAsync(mOuter.resolver(), 1);

				destinations.Count.ShouldBe(1);
				destinations.Single().Ref.ShouldBe($"s:{newest}");
			}

			[Fact]
			public async Task TheSameTokenForTwoDifferentUsers_IsTwoDestinations()
			{
				// A shared device that two people logged in on in turn: each is notified about their own things.
				await mOuter.addSessionAsync(1, "tok-shared");
				await mOuter.addSessionAsync(2, "tok-shared");

				(await mOuter.resolveAsync(mOuter.resolver(), 1, 2)).Count.ShouldBe(2);
			}

			[Fact]
			public async Task NoUsers_NoQuery_NoDestinations()
			{
				await mOuter.addSessionAsync(1, "tok-1");

				(await mOuter.resolveAsync(mOuter.resolver())).ShouldBeEmpty();
			}

			[Fact]
			public async Task TheAccountComesFromTheAppIdOfTheSession_ElseTheDefault_ElseNull()
			{
				await mOuter.addSessionAsync(1, "tok-mf5", appUid: "com.fleetistics.mf5");
				await mOuter.addSessionAsync(1, "tok-pssp", appUid: "com.fleetistics.pssp");
				await mOuter.addSessionAsync(1, "tok-other", appUid: "com.somebody.else");

				var withDefault = await mOuter.resolveAsync(mOuter.resolver(o =>
				{
					o.AccountsByAppId["com.fleetistics.mf5"] = "mf5";
					o.AccountsByAppId["com.fleetistics.pssp"] = "pssp";
					o.DefaultAccount = "fallback";
				}), 1);
				withDefault.Single(d => d.Token == "tok-mf5").Account.ShouldBe("mf5");
				withDefault.Single(d => d.Token == "tok-pssp").Account.ShouldBe("pssp");
				withDefault.Single(d => d.Token == "tok-other").Account.ShouldBe("fallback");

				var withoutDefault = await mOuter.resolveAsync(mOuter.resolver(o => o.AccountsByAppId["com.fleetistics.mf5"] = "mf5"), 1);
				withoutDefault.Single(d => d.Token == "tok-other").Account.ShouldBeNull(); // the sender's own default for this host
			}
		}

		public class Invalidating : IAsyncDisposable
		{
			private readonly UserSessionFcmHooksTests mOuter = new();

			public ValueTask DisposeAsync() => mOuter.DisposeAsync();

			private static UserSessionFcmTokenInvalidator invalidator() => new(NullLogger<UserSessionFcmTokenInvalidator>.Instance);

			private async Task<bool> invalidateAsync(int userId, string token, string? @ref = null)
			{
				using var repository = mOuter.mDatabase.CreateRepository();
				return await invalidator().InvalidateAsync(repository, userId, @ref, token, CancellationToken.None);
			}

			private Task<Dictionary<int, string>> tokensAsync() =>
				mOuter.mDatabase.QueryAsync(async db => await db.Set<UserSession>().AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.ClientInfo.FCM_FID));

			[Fact]
			public async Task ClearsTheDeadToken_AndNothingElse()
			{
				var dead = await mOuter.addSessionAsync(1, "tok-dead");
				var alive = await mOuter.addSessionAsync(1, "tok-alive");

				(await invalidateAsync(1, "tok-dead")).ShouldBeTrue();

				var tokens = await tokensAsync();
				tokens[dead].ShouldBe("");
				tokens[alive].ShouldBe("tok-alive");
			}

			[Fact]
			public async Task ClearsEverySessionOfTheUserThatCarriesIt()
			{
				var first = await mOuter.addSessionAsync(1, "tok-dead");
				var second = await mOuter.addSessionAsync(1, "tok-dead");

				await invalidateAsync(1, "tok-dead");

				(await tokensAsync()).Values.ShouldAllBe(t => t == "");
				(await tokensAsync()).Keys.ShouldBe([first, second], ignoreOrder: true);
			}

			[Fact]
			public async Task LeavesAnotherUsersSessionAlone_EvenWithTheSameToken()
			{
				await mOuter.addSessionAsync(1, "tok-shared");
				var theirs = await mOuter.addSessionAsync(2, "tok-shared");

				await invalidateAsync(1, "tok-shared");

				(await tokensAsync())[theirs].ShouldBe("tok-shared");
			}

			[Fact]
			public async Task ATokenTheClientHasRefreshedSince_NoLongerMatches_AndIsLeftAlone()
			{
				var session = await mOuter.addSessionAsync(1, "tok-new-after-refresh");

				(await invalidateAsync(1, "tok-old-and-dead")).ShouldBeFalse();

				(await tokensAsync())[session].ShouldBe("tok-new-after-refresh");
			}

			[Fact]
			public async Task NeverDeactivatesTheSession_OnlyClearsTheToken()
			{
				var session = await mOuter.addSessionAsync(1, "tok-dead");

				await invalidateAsync(1, "tok-dead");

				var status = await mOuter.mDatabase.QueryAsync(db => db.Set<UserSession>().Where(s => s.Id == session).Select(s => s.StatusId).SingleAsync());
				status.ShouldBe(UserSessionStatus.Active);
			}

			[Fact]
			public async Task AnEmptyDestination_IsNeverMatchedAgainstSessionsWithNoToken()
			{
				// Without the guard "clear every session whose token is empty" would be a harmless no-op, but
				// the point is that an empty value must never be treated as a token to look for.
				await mOuter.addSessionAsync(1, "");

				(await invalidateAsync(1, "")).ShouldBeFalse();
			}
		}
	}
}
