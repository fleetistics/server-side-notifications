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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Notification = exs.notifications_model.Notifications.Notification;

namespace exs.notifications_pipeline.E2ETests
{
	/// <summary>
	/// Email end to end, with both halves real: a host with the email transport on (the fan-out, its address book,
	/// the outbox, the email relay, the result puller) talking over HTTP to the real exs.sender-email app (intake,
	/// queue, dispatcher, email channel, result log). Only the SMTP server is a fake.
	/// </summary>
	public sealed class EmailPipelineE2ETests : IAsyncLifetime
	{
		private HostDatabase mDatabase = null!;
		private EmailSenderAppHost mMailSender = null!;
		private FcmSenderAppHost? mPushSender;
		private readonly TestEmailResolver mAddressBook = new();
		private readonly TestEmailInvalidator mInvalidator = new();
		private ServiceProvider? mHost;
		private readonly List<IHostedService> mStarted = new();

		public async ValueTask InitializeAsync()
		{
			mDatabase = HostDatabase.Create();
			mMailSender = await EmailSenderAppHost.StartAsync();
		}

		public async ValueTask DisposeAsync()
		{
			foreach (var hosted in Enumerable.Reverse(mStarted))
			{
				try { await hosted.StopAsync(CancellationToken.None); } catch { /* shutting down a test */ }
			}
			if (mHost != null) await mHost.DisposeAsync();
			if (mPushSender != null) await mPushSender.DisposeAsync();
			await mMailSender.DisposeAsync();
			await mDatabase.DisposeAsync();
		}

		/// <summary>Builds the host as an api-server would wire it, and starts all of its hosted services.</summary>
		private async Task startHostAsync(bool withPush = false, bool withInvalidator = true, params (string Key, string Value)[] overrides)
		{
			var values = new Dictionary<string, string?>
			{
				["NotificationService:EmailTransport"] = "Sender",
				["NotificationService:EmailSender:BaseUrl"] = "http://mail.sender.test",
				["NotificationService:EmailSender:ApiKey"] = EmailSenderAppHost.ApiKey,
				["NotificationService:EmailSender:ResultPollIntervalSeconds"] = "1",
				["NotificationService:EmailSender:RelayPollIntervalSeconds"] = "60",
			};
			if (withPush)
			{
				mPushSender = await FcmSenderAppHost.StartAsync();
				values["NotificationService:FcmTransport"] = "Sender";
				values["NotificationService:FcmSender:BaseUrl"] = "http://push.sender.test";
				values["NotificationService:FcmSender:ApiKey"] = FcmSenderAppHost.ApiKey;
				values["NotificationService:FcmSender:ResultPollIntervalSeconds"] = "1";
				values["NotificationService:FcmSender:RelayPollIntervalSeconds"] = "60";
			}
			foreach (var (key, value) in overrides) values[key] = value;
			var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

			var services = new ServiceCollection();
			mDatabase.Register(services);
			services.AddNotificationSender(configuration);
			services.AddSingleton<IEmailDestinationResolver>(mAddressBook);
			if (withInvalidator) services.AddSingleton<IEmailAddressInvalidator>(mInvalidator);
			var mailHandler = mMailSender.CreateHandler();
			services.AddHttpClient(HttpNotificationTransport.EmailClientName).ConfigurePrimaryHttpMessageHandler(() => mailHandler);
			if (withPush)
			{
				services.AddUserSessionFcmHooks();
				var pushHandler = mPushSender!.CreateHandler();
				services.AddHttpClient(HttpNotificationTransport.ClientName).ConfigurePrimaryHttpMessageHandler(() => pushHandler);
			}
			mHost = services.BuildServiceProvider();

			foreach (var hosted in mHost.GetServices<IHostedService>())
			{
				await hosted.StartAsync(CancellationToken.None);
				mStarted.Add(hosted);
			}
			await Task.Delay(100); // the fan-out's startup catch-up pass, over an empty table
		}

		private async Task addDeviceAsync(int userId, string token)
		{
			await using var context = mDatabase.CreateContext();
			context.Set<UserSession>().Add(new UserSession
			{
				UserId = userId,
				StatusId = UserSessionStatus.Active,
				ClientInfo = new SessionClientInfo { FCM_FID = token, PlatformId = ClientDevicePlatform.Android, AppUID = "app" },
			});
			await context.SaveChangesAsync();
		}

		/// <summary>An email notification: what a host sends when a user should be mailed about something.</summary>
		private async Task notifyAsync(int[] userIds, string title = "Search Party Joined", string body = "Sam joined the party", short type = 3)
		{
			using var scope = mHost!.CreateScope();
			var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
			scope.ServiceProvider.GetRequiredService<INotificationSender>().CreateEmailNotification(repository, type, 55, title, body, "{}", userIds);
			await repository.SaveAsync();
		} // disposing the scope hands the notification to the fan-out

		private async Task pushAsync(int[] userIds, string title = "Search Party Joined", string body = "Sam joined the party", short type = 3)
		{
			using var scope = mHost!.CreateScope();
			var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
			scope.ServiceProvider.GetRequiredService<INotificationSender>().CreateMobileNotification(repository, type, 55, title, body, "{}", userIds);
			await repository.SaveAsync();
		}

		/// <summary>A free-form email, as an api-server would send a password reset: no notification, no user.</summary>
		private async Task sendEmailAsync(EmailMessage message)
		{
			using var scope = mHost!.CreateScope();
			var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
			scope.ServiceProvider.GetRequiredService<INotificationSender>().CreateEmail(repository, message);
			await repository.SaveAsync();
		} // disposing the scope wakes the email relay

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
			throw new TimeoutException($"Timed out waiting for: {what}. Rows: {string.Join("; ", rows.Select(r => $"#{r.Id} {r.Channel} state {r.State} {r.ResultStatus} {r.SubmitError}"))}");
		}

		private Task<List<OutboxMessage>> waitForCompletedAsync(int count, string? channel = null) =>
			waitForRowsAsync(rows => rows.Count(r => r.State == OutboxState.Completed && (channel == null || r.Channel == channel)) >= count, $"{count} outbox row(s) to complete");

		[Fact]
		public async Task ANotification_IsEmailedToTheUser_AndTheDeliveryRecordSaysSo()
		{
			mAddressBook.Users[7] = ("sam@example.com", "Sam Rivera", null);
			await startHostAsync();

			await notifyAsync([7]);
			var rows = await waitForCompletedAsync(1, "email");

			var row = rows.Single(r => r.Channel == "email");
			row.ResultStatus.ShouldBe(ResultStatuses.Sent);
			row.Account.ShouldBeNull(); // the host named none, so the email sender's default for this host was used
			row.Ref.ShouldBe("u:7");
			row.Attempts.ShouldBe(1);
			row.ErrorKind.ShouldBeNull();
			row.DestinationInvalid.ShouldBeFalse();
			row.Envelope.ShouldBeNull(); // the address and the text are not kept once the sender has them

			var call = mMailSender.Gateway.Calls.ShouldHaveSingleItem();
			call.Account.ShouldBe("noreply");
			call.Message.From.Mailboxes.Single().Address.ShouldBe("noreply@example.com");
			call.Message.To.Mailboxes.Single().Address.ShouldBe("sam@example.com");
			call.Message.To.Mailboxes.Single().Name.ShouldBe("Sam Rivera");
			call.Message.Subject.ShouldBe("Search Party Joined");
			call.Message.TextBody!.TrimEnd().ShouldBe("Sam joined the party");
			row.ProviderMessageId.ShouldBe(call.Message.MessageId);
		}

		[Fact]
		public async Task TheHostsAccountChoice_DecidesWhoTheMailIsFrom()
		{
			mAddressBook.Users[7] = ("sam@example.com", null, "support");
			await startHostAsync();

			await notifyAsync([7]);
			var rows = await waitForCompletedAsync(1, "email");

			rows.Single().Account.ShouldBe("support");
			mMailSender.Gateway.Calls.Single().Message.From.Mailboxes.Single().Address.ShouldBe("support@example.com");
		}

		[Fact]
		public async Task AnAccountTheSenderDoesNotAllowTheHost_IsRefusedAtTheDoor_AndRecordedOnTheRow()
		{
			mAddressBook.Users[7] = ("sam@example.com", null, "restricted");
			await startHostAsync();

			await notifyAsync([7]);
			var rows = await waitForRowsAsync(r => r.Any(x => x.State == OutboxState.Rejected), "the sender's refusal");

			rows.Single().SubmitError!.ShouldContain("account_not_allowed");
			mMailSender.Gateway.Calls.ShouldBeEmpty();
		}

		[Fact]
		public async Task ANonexistentMailbox_IsRecordedDead_AndTheHostIsToldSoItCanStopUsingTheAddress()
		{
			mAddressBook.Users[7] = ("ghost@example.com", null, null);
			await startHostAsync();
			mMailSender.Gateway.Behavior = (_, _) => throw FakeEmailGateway.RecipientRefused(550, "550 5.1.1 The email account does not exist");

			await notifyAsync([7]);
			var rows = await waitForCompletedAsync(1, "email");

			var row = rows.Single();
			row.ResultStatus.ShouldBe(ResultStatuses.Failed);
			row.ErrorKind.ShouldBe(ErrorKinds.DestinationInvalid);
			row.DestinationInvalid.ShouldBeTrue();
			row.ErrorCode.ShouldBe("550 5.1.1");
			await eventuallyAsync(() => mInvalidator.Calls.Count == 1, "the host's invalidator to be called");
			mInvalidator.Calls.Single().ShouldBe((7, "u:7", "ghost@example.com"));
		}

		[Fact]
		public async Task APolicyRefusal_IsRecorded_ButTheAddressIsNotReportedDead()
		{
			mAddressBook.Users[7] = ("sam@example.com", null, null);
			await startHostAsync();
			mMailSender.Gateway.Behavior = (_, _) => throw FakeEmailGateway.RecipientRefused(554, "554 5.7.1 Message rejected as spam");

			await notifyAsync([7]);
			var row = (await waitForCompletedAsync(1, "email")).Single();
			await Task.Delay(300);

			row.ResultStatus.ShouldBe(ResultStatuses.Failed);
			row.ErrorKind.ShouldBe(ErrorKinds.Permanent);
			row.DestinationInvalid.ShouldBeFalse();
			mInvalidator.Calls.ShouldBeEmpty();
		}

		[Fact]
		public async Task AHostWithoutAnInvalidator_StillGetsTheBounceRecorded()
		{
			mAddressBook.Users[7] = ("ghost@example.com", null, null);
			await startHostAsync(withInvalidator: false);
			mMailSender.Gateway.Behavior = (_, _) => throw FakeEmailGateway.RecipientRefused(550, "550 5.1.1 No such user");

			await notifyAsync([7]);
			var row = (await waitForCompletedAsync(1, "email")).Single();

			row.DestinationInvalid.ShouldBeTrue();
		}

		[Fact]
		public async Task AUserWithNoAddress_IsRecordedAsNotEmailed_AndNothingIsSent()
		{
			await startHostAsync();

			await notifyAsync([7]);
			var rows = await waitForCompletedAsync(1, "email");

			var row = rows.Single();
			row.ErrorKind.ShouldBe(OutboxErrorKind.NoDestination);
			row.ErrorCode.ShouldBe(OutboxErrorCode.NoEmailAddress);
			mMailSender.Gateway.Calls.ShouldBeEmpty();
		}

		[Fact]
		public async Task AMobileNotification_SendsNoEmail_EvenThoughTheUserHasAnAddressAndEmailIsOn()
		{
			mAddressBook.Users[7] = ("sam@example.com", null, null);
			await startHostAsync();

			await pushAsync([7]);
			await eventuallyAsync(async () => (await mDatabase.QueryAsync(db => db.Set<Notification>().SingleAsync())).Status == NotificationStatus.Processed, "the notification to be marked processed");
			await Task.Delay(300);

			(await rowsAsync()).ShouldBeEmpty();
			mMailSender.Gateway.Calls.ShouldBeEmpty();
		}

		[Fact]
		public async Task AFreeFormEmail_IsSubmittedAtOnce_ItsEnvelopeIsCleared_AndTheResultIsRecorded_WithNoNotificationAnywhere()
		{
			await startHostAsync(); // the relay polls every 60 seconds: only the wake-up after the commit gets this out in time

			await sendEmailAsync(new EmailMessage
			{
				To = "pat@example.com",
				ToName = "Pat Doe",
				Subject = "Reset your password",
				Text = "Open https://example.com/reset?token=abc123 to choose a new one.",
				Html = "<p>Open <a href=\"https://example.com/reset?token=abc123\">this link</a>.</p>",
				ReplyTo = "help@example.com",
				Priority = MessagePriorities.High,
				Account = "support",
				Ref = "reset:42",
			});
			var rows = await waitForCompletedAsync(1, "email");

			var row = rows.ShouldHaveSingleItem();
			row.NotificationId.ShouldBeNull();
			row.UserId.ShouldBeNull();
			row.ResultStatus.ShouldBe(ResultStatuses.Sent);
			row.Account.ShouldBe("support");
			row.Ref.ShouldBe("reset:42");
			row.Attempts.ShouldBe(1);
			row.SubmittedAt.ShouldNotBeNull();
			row.Envelope.ShouldBeNull(); // the address and the reset link are not kept once the sender has them

			var call = mMailSender.Gateway.Calls.ShouldHaveSingleItem();
			call.Account.ShouldBe("support");
			call.Message.From.Mailboxes.Single().Address.ShouldBe("support@example.com");
			call.Message.To.Mailboxes.Single().Address.ShouldBe("pat@example.com");
			call.Message.To.Mailboxes.Single().Name.ShouldBe("Pat Doe");
			call.Message.ReplyTo.Mailboxes.Single().Address.ShouldBe("help@example.com");
			call.Message.Subject.ShouldBe("Reset your password");
			call.Message.TextBody!.ShouldContain("token=abc123");
			call.Message.HtmlBody!.ShouldContain("this link");
			row.ProviderMessageId.ShouldBe(call.Message.MessageId);

			// The point of CreateEmail: nothing about this mail is in the append-only notification log.
			(await mDatabase.QueryAsync(db => db.Set<Notification>().CountAsync())).ShouldBe(0);
			(await mDatabase.QueryAsync(db => db.Set<NotificationToUser>().CountAsync())).ShouldBe(0);
		}

		[Fact]
		public async Task AFreeFormEmailToAMailboxThatDoesNotExist_IsRecordedDead_WithoutTheHostsInvalidatorBeingCalled()
		{
			await startHostAsync();
			mMailSender.Gateway.Behavior = (_, _) => throw FakeEmailGateway.RecipientRefused(550, "550 5.1.1 The email account does not exist");

			await sendEmailAsync(new EmailMessage { To = "ghost@example.com", Subject = "Welcome", Text = "Hello" });
			var row = (await waitForCompletedAsync(1, "email")).Single();
			await Task.Delay(300); // time for the puller to have called the invalidator, were it going to

			row.DestinationInvalid.ShouldBeTrue();
			row.ErrorKind.ShouldBe(ErrorKinds.DestinationInvalid);
			row.UserId.ShouldBeNull();
			mInvalidator.Calls.ShouldBeEmpty(); // it is keyed by user, and this mail has none
		}

		[Fact]
		public async Task AWrongApiKey_NeverBurnsTheMail_ItStaysQueuedForWhenTheKeyIsFixed()
		{
			mAddressBook.Users[7] = ("sam@example.com", null, null);
			await startHostAsync(overrides: ("NotificationService:EmailSender:ApiKey", "not-the-key"));

			await notifyAsync([7]);
			var rows = await waitForRowsAsync(r => r.Any(x => x.SubmitError != null), "the sender's 401 to be recorded");

			var row = rows.Single();
			row.State.ShouldBe(OutboxState.Pending);
			row.Envelope.ShouldNotBeNull();
			mMailSender.Gateway.Calls.ShouldBeEmpty();
		}

		[Fact]
		public async Task PushAndEmailTogether_EachNotificationReachesItsOwnChannel_TheDeviceAndTheInbox()
		{
			mAddressBook.Users[7] = ("sam@example.com", "Sam", null);
			await addDeviceAsync(7, "tok-android");
			await startHostAsync(withPush: true);

			await pushAsync([7]);
			await notifyAsync([7]);
			var rows = await waitForRowsAsync(r => r.Count(x => x.State == OutboxState.Completed) >= 2, "both channels to complete");

			rows.Single(r => r.Channel == "fcm").ResultStatus.ShouldBe(ResultStatuses.Sent);
			rows.Single(r => r.Channel == "email").ResultStatus.ShouldBe(ResultStatuses.Sent);
			mPushSender!.Gateway.Calls.ShouldHaveSingleItem().Token.ShouldBe("tok-android");
			mMailSender.Gateway.Calls.ShouldHaveSingleItem().Message.To.Mailboxes.Single().Address.ShouldBe("sam@example.com");
			// Each channel read its own results from its own sender; neither disturbed the other.
			(await mDatabase.QueryAsync(db => db.Set<TransportCursor>().AsNoTracking().Select(c => c.Name).ToListAsync()))
				.ShouldBe(["email-sender", "fcm-sender"], ignoreOrder: true);
			await eventuallyAsync(async () => (await mDatabase.QueryAsync(db => db.Set<Notification>().ToListAsync())).All(n => n.Status == NotificationStatus.Processed), "the notifications to be marked processed");
		}

		private static async Task eventuallyAsync(Func<bool> condition, string what) =>
			await eventuallyAsync(() => Task.FromResult(condition()), what);

		private static async Task eventuallyAsync(Func<Task<bool>> condition, string what)
		{
			var deadline = DateTime.UtcNow.AddSeconds(15);
			while (DateTime.UtcNow < deadline)
			{
				if (await condition()) return;
				await Task.Delay(25);
			}
			throw new TimeoutException($"Timed out waiting for: {what}");
		}
	}
}
