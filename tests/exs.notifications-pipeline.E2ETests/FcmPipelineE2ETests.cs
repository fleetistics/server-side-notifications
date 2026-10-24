using exs.Database.Commons.Interfaces;
using exs.modelCommons.AppStructure;
using exs.modelCommons.UserManagement;
using exs.notifications_contracts;
using exs.notifications_model.Notifications;
using exs.notifications_model.Outbox;
using exs.notifications_pipeline.E2ETests.Support;
using exs.notifications_service;
using exs.notifications_service.Interfaces;
using exs.notifications_service.Sender;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Notification = exs.notifications_model.Notifications.Notification;

namespace exs.notifications_pipeline.E2ETests
{
	/// <summary>
	/// The new pipeline end to end, with both halves real: a host in "Sender" mode (NotificationSender, the
	/// fan-out, the user-session hooks, the outbox, the relay, the result puller) talking over HTTP to the real
	/// exs.sender-fcm app (intake, queue, dispatcher, FCM channel, result log). Only Firebase itself is a fake,
	/// and the "network" between the two can be made to fail.
	/// </summary>
	public sealed class FcmPipelineE2ETests : IAsyncLifetime
	{
		private HostDatabase mDatabase = null!;
		private FcmSenderAppHost mSender = null!;
		private FlakyNetwork mNetwork = null!;
		private ServiceProvider? mHost;
		private readonly List<IHostedService> mStarted = new();

		public async ValueTask InitializeAsync()
		{
			mDatabase = HostDatabase.Create();
			mSender = await FcmSenderAppHost.StartAsync();
			mNetwork = new FlakyNetwork(mSender.CreateHandler());
		}

		public async ValueTask DisposeAsync()
		{
			foreach (var hosted in Enumerable.Reverse(mStarted))
			{
				try { await hosted.StopAsync(CancellationToken.None); } catch { /* shutting down a test */ }
			}
			if (mHost != null) await mHost.DisposeAsync();
			await mSender.DisposeAsync();
			await mDatabase.DisposeAsync();
		}

		/// <summary>Builds the host as an api-server would wire it, and starts all of its hosted services.</summary>
		private async Task startHostAsync(params (string Key, string Value)[] overrides)
		{
			var values = new Dictionary<string, string?>
			{
				["NotificationService:FcmTransport"] = "Sender",
				["NotificationService:FcmSender:BaseUrl"] = "http://sender.test",
				["NotificationService:FcmSender:ApiKey"] = FcmSenderAppHost.ApiKey,
				["NotificationService:FcmSender:ResultPollIntervalSeconds"] = "1",
				["NotificationService:FcmSender:RelayPollIntervalSeconds"] = "60",
			};
			foreach (var (key, value) in overrides) values[key] = value;
			var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

			var services = new ServiceCollection();
			mDatabase.Register(services);
			services.AddNotificationSender(configuration);
			services.AddUserSessionFcmHooks();
			services.AddHttpClient(HttpNotificationTransport.ClientName).ConfigurePrimaryHttpMessageHandler(() => mNetwork);
			mHost = services.BuildServiceProvider();

			foreach (var hosted in mHost.GetServices<IHostedService>())
			{
				await hosted.StartAsync(CancellationToken.None);
				mStarted.Add(hosted);
			}
			await Task.Delay(100); // the fan-out's startup catch-up pass, over an empty table
		}

		private async Task<int> addSessionAsync(int userId, string token, short platform = ClientDevicePlatform.Android, string appUid = "app")
		{
			await using var context = mDatabase.CreateContext();
			var session = new UserSession
			{
				UserId = userId,
				StatusId = UserSessionStatus.Active,
				ClientInfo = new SessionClientInfo { FCM_FID = token, PlatformId = platform, AppUID = appUid },
			};
			context.Set<UserSession>().Add(session);
			await context.SaveChangesAsync();
			return session.Id;
		}

		private async Task notifyAsync(int[] userIds, string title = "Search Party Joined", string body = "Sam joined the party", string? payload = """{ "searchPartyId": 55 }""", short type = 3, int entityId = 55)
		{
			using var scope = mHost!.CreateScope();
			var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
			scope.ServiceProvider.GetRequiredService<INotificationSender>().CreateMobileNotification(repository, type, entityId, title, body, payload!, userIds);
			await repository.SaveAsync();
		} // disposing the scope hands the notification to the fan-out

		private Task<List<OutboxMessage>> rowsAsync() => mDatabase.QueryAsync(db => db.Set<OutboxMessage>().AsNoTracking().OrderBy(o => o.Id).ToListAsync());

		private async Task<List<OutboxMessage>> waitForRowsAsync(Func<List<OutboxMessage>, bool> condition, string what, int timeoutMilliseconds = 20_000)
		{
			var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
			List<OutboxMessage> rows = [];
			while (DateTime.UtcNow < deadline)
			{
				rows = await rowsAsync();
				if (condition(rows)) return rows;
				await Task.Delay(50);
			}
			throw new TimeoutException($"Timed out waiting for: {what}. Rows: {string.Join("; ", rows.Select(r => $"#{r.Id} state {r.State} {r.ResultStatus} {r.SubmitError}"))}");
		}

		private Task<List<OutboxMessage>> waitForCompletedAsync(int count) =>
			waitForRowsAsync(rows => rows.Count(r => r.State == OutboxState.Completed) >= count, $"{count} outbox row(s) to complete");

		/// <summary>
		/// The fan-out commits the outbox rows first and flips the notification to Processed straight after, so a
		/// test that has seen the rows must still wait for the status rather than read it at once.
		/// </summary>
		private async Task shouldBecomeProcessedAsync()
		{
			var deadline = DateTime.UtcNow.AddSeconds(10);
			byte status = NotificationStatus.New;
			while (DateTime.UtcNow < deadline)
			{
				status = (await mDatabase.QueryAsync(db => db.Set<Notification>().SingleAsync())).Status;
				if (status == NotificationStatus.Processed) return;
				await Task.Delay(25);
			}
			status.ShouldBe(NotificationStatus.Processed);
		}

		private Task<Dictionary<int, string>> tokensAsync() =>
			mDatabase.QueryAsync(async db => await db.Set<UserSession>().AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.ClientInfo.FCM_FID));

		[Fact]
		public async Task ANotification_ReachesEveryActiveDeviceOfTheUser_AndEachDeviceGetsItsDeliveryRecord()
		{
			var androidSession = await addSessionAsync(7, "tok-android", ClientDevicePlatform.Android);
			var iosSession = await addSessionAsync(7, "tok-ios", ClientDevicePlatform.Ios);
			await startHostAsync();

			await notifyAsync([7], payload: """{ "searchPartyId": 55, "badge": 4, "iosSound": "alert.caf", "androidSound": "alert" }""");
			var rows = await waitForCompletedAsync(2);

			// The delivery record, per device.
			rows.ShouldAllBe(r => r.ResultStatus == ResultStatuses.Sent && r.Attempts == 1 && r.ErrorKind == null && !r.DestinationInvalid);
			rows.ShouldAllBe(r => r.ProviderMessageId!.StartsWith("projects/e2e/messages/"));
			rows.ShouldAllBe(r => r.Envelope == null); // the destination and text are not kept once the sender has them
			rows.Select(r => r.Ref).ShouldBe([$"s:{androidSession}", $"s:{iosSession}"], ignoreOrder: true);

			// What Firebase was actually asked to send.
			var calls = mSender.Gateway.Calls;
			calls.Count.ShouldBe(2);
			var android = calls.Single(c => c.Token == "tok-android").Message;
			android.Android!.Notification.Sound.ShouldBe("alert");
			android.Apns.ShouldBeNull();
			android.Notification.Title.ShouldBe("Search Party Joined");
			var ios = calls.Single(c => c.Token == "tok-ios").Message;
			ios.Apns!.Aps.Badge.ShouldBe(4);
			ios.Apns.Aps.Sound.ShouldBe("alert.caf");
			ios.Android.ShouldBeNull();
			ios.Data!["searchPartyId"].ShouldBe("55");
			ios.Data["TypeId"].ShouldBe("3");
			ios.Data["EntityId"].ShouldBe("55");

			// And the bookkeeping around it.
			await shouldBecomeProcessedAsync();
			(await mDatabase.QueryAsync(db => db.Set<TransportCursor>().SingleAsync(c => c.Name == ResultPuller.CursorName))).Position.ShouldBeGreaterThan(0);
		}

		[Fact]
		public async Task ADeadToken_IsClearedFromTheUsersSession_AndNeverTriedAgain()
		{
			var dead = await addSessionAsync(7, "tok-dead");
			var good = await addSessionAsync(7, "tok-good");
			mSender.Gateway.Behavior = (n, call) => call.Token == "tok-dead"
				? throw FirebaseExceptions.Create(MessagingErrorCode.Unregistered, ErrorCode.NotFound, "Requested entity was not found.")
				: Task.FromResult($"projects/e2e/messages/{n}");
			await startHostAsync();

			await notifyAsync([7]);
			var first = await waitForCompletedAsync(2);

			var deadRow = first.Single(r => r.DestinationInvalid);
			deadRow.ResultStatus.ShouldBe(ResultStatuses.Failed);
			deadRow.ErrorKind.ShouldBe(ErrorKinds.DestinationInvalid);
			deadRow.ErrorCode.ShouldBe("Unregistered");
			first.Single(r => !r.DestinationInvalid).ResultStatus.ShouldBe(ResultStatuses.Sent);

			// The host acted on it: the dead token is gone from the session, the good one is untouched.
			Dictionary<int, string> tokens = [];
			var deadline = DateTime.UtcNow.AddSeconds(10);
			while (DateTime.UtcNow < deadline && (tokens = await tokensAsync())[dead] != "") await Task.Delay(50);
			tokens[dead].ShouldBe("");
			tokens[good].ShouldBe("tok-good");

			// The next notification no longer goes to the dead device at all.
			await notifyAsync([7]);
			await waitForCompletedAsync(3);
			mSender.Gateway.Calls.Count(c => c.Token == "tok-dead").ShouldBe(1);
			mSender.Gateway.Calls.Count(c => c.Token == "tok-good").ShouldBe(2);
		}

		[Fact]
		public async Task AWrongAccountMapping_IsReportedAsAFailure_ButTheTokenIsNotErased()
		{
			var session = await addSessionAsync(7, "tok-fine", appUid: "com.fleetistics.pssp");
			mSender.Gateway.Behavior = (_, _) => throw FirebaseExceptions.Create(MessagingErrorCode.SenderIdMismatch, ErrorCode.PermissionDenied);
			await startHostAsync(("NotificationService:FcmSender:AccountsByAppId:com.fleetistics.pssp", "other"));

			await notifyAsync([7]);
			var row = (await waitForCompletedAsync(1)).Single();

			row.Account.ShouldBe("other");
			mSender.Gateway.Calls.Single().Account.ShouldBe("other");
			row.ResultStatus.ShouldBe(ResultStatuses.Failed);
			row.ErrorKind.ShouldBe(ErrorKinds.Permanent);
			row.ErrorCode.ShouldBe("SenderIdMismatch");
			row.DestinationInvalid.ShouldBeFalse();
			await Task.Delay(500); // time for any (wrong) invalidation to have happened
			(await tokensAsync())[session].ShouldBe("tok-fine");
		}

		[Fact]
		public async Task AUserWithNoDevice_IsRecordedAsSuch_AndNothingIsSent()
		{
			await startHostAsync();

			await notifyAsync([99]);
			var row = (await waitForCompletedAsync(1)).Single();

			row.UserId.ShouldBe(99);
			row.ResultStatus.ShouldBe(ResultStatuses.Failed);
			row.ErrorKind.ShouldBe(OutboxErrorKind.NoDestination);
			mSender.Gateway.Calls.ShouldBeEmpty();
			await shouldBecomeProcessedAsync();
		}

		[Fact]
		public async Task WhenTheSenderCannotBeReached_NothingIsLost_AndEverythingGoesOutOnceItIsBack()
		{
			await addSessionAsync(7, "tok-1");
			await startHostAsync();
			mNetwork.Down = true;

			await notifyAsync([7]);
			var waiting = await waitForRowsAsync(rows => rows.Any(r => r.SubmitError != null), "the relay to notice the sender is unreachable");
			waiting.Single().State.ShouldBe(OutboxState.Pending);
			waiting.Single().Envelope.ShouldNotBeNull(); // still holding the message
			mSender.Gateway.Calls.ShouldBeEmpty();

			mNetwork.Down = false;
			var rows = await waitForCompletedAsync(1);

			rows.Single().ResultStatus.ShouldBe(ResultStatuses.Sent);
			rows.Single().SubmitError.ShouldBeNull();
			mSender.Gateway.Calls.Count.ShouldBe(1);
		}

		[Fact]
		public async Task AnAnswerLostOnTheWay_DoesNotMakeTheDeviceGetTheNotificationTwice()
		{
			// The sender received and stored the message, but the host never heard back, so it submits again.
			// The sender recognises the message id and answers "duplicate"; Firebase is called exactly once.
			await addSessionAsync(7, "tok-1");
			await startHostAsync();
			mNetwork.LoseTheNextAnswer();

			await notifyAsync([7]);
			var rows = await waitForCompletedAsync(1);

			rows.Single().ResultStatus.ShouldBe(ResultStatuses.Sent);
			await Task.Delay(500);
			mSender.Gateway.Calls.Count.ShouldBe(1);
		}

		[Fact]
		public async Task AWrongApiKey_KeepsTheMessagesQueued_RatherThanRejectingThem()
		{
			await addSessionAsync(7, "tok-1");
			await startHostAsync(("NotificationService:FcmSender:ApiKey", "not-the-key"));

			await notifyAsync([7]);
			var rows = await waitForRowsAsync(r => r.Any(x => x.SubmitError != null && x.SubmitError.Contains("401")), "the relay to be told its key is wrong");

			// One config change fixes this for every message, so none may be given up on.
			rows.Single().State.ShouldBe(OutboxState.Pending);
			rows.Single().Envelope.ShouldNotBeNull();
			mSender.Gateway.Calls.ShouldBeEmpty();
		}

		[Fact]
		public async Task AnAccountTheHostMayNotUse_IsRefusedByTheSender_AndRecordedAsRejected()
		{
			await addSessionAsync(7, "tok-1");
			await startHostAsync(("NotificationService:FcmSender:DefaultAccount", "restricted"));

			await notifyAsync([7]);
			var rows = await waitForRowsAsync(r => r.Any(x => x.State == OutboxState.Rejected), "the sender to refuse the account");

			rows.Single().SubmitError!.ShouldContain("account_not_allowed");
			rows.Single().Envelope.ShouldBeNull();
			mSender.Gateway.Calls.ShouldBeEmpty();
		}

		[Fact]
		public async Task SeveralUsersAndDevices_AreAllDeliveredExactlyOnce()
		{
			var expectedTokens = new List<string>();
			for (var user = 1; user <= 5; user++)
			{
				for (var device = 1; device <= 2; device++)
				{
					var token = $"tok-{user}-{device}";
					expectedTokens.Add(token);
					await addSessionAsync(user, token, device == 1 ? ClientDevicePlatform.Android : ClientDevicePlatform.Ios);
				}
			}
			await startHostAsync();

			await notifyAsync([1, 2, 3, 4, 5, 6]); // user 6 has no device
			var rows = await waitForCompletedAsync(11);

			rows.Count(r => r.ResultStatus == ResultStatuses.Sent).ShouldBe(10);
			rows.Single(r => r.ErrorKind == OutboxErrorKind.NoDestination).UserId.ShouldBe(6);
			mSender.Gateway.Calls.Select(c => c.Token).OrderBy(t => t).ShouldBe(expectedTokens.OrderBy(t => t));
		}
	}
}
