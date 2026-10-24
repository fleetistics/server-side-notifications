using System.Diagnostics;
using exs.Database.Commons.Interfaces;
using exs.notifications_contracts;
using exs.notifications_model.Notifications;
using exs.notifications_model.Outbox;
using exs.notifications_model.Transports;
using exs.notifications_service.Interfaces;
using exs.notifications_service.Sender;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace exs.notifications_service.Tests.Sender
{
	/// <summary>
	/// INotificationSender.CreateEmail through the real registration: one outbox row in the caller's transaction, and
	/// nothing else. No notification, no recipient list - an address and a reset link do not belong in a log that is
	/// never pruned.
	/// </summary>
	public sealed class FreeFormEmailTests
	{
		/// <summary>A host with the email transport on (and whatever else a test adds), without its relays: the rows can be looked at before anything submits them.</summary>
		private sealed class Host : IAsyncDisposable
		{
			public HostTestDatabase Database { get; } = HostTestDatabase.Create();
			public ManualTimeProvider Clock { get; } = new();
			public ServiceProvider Provider { get; }

			public Host(bool emailOn = true, params (string Key, string Value)[] more)
			{
				var values = new Dictionary<string, string?>();
				if (emailOn)
				{
					values["NotificationService:EmailTransport"] = "Sender";
					values["NotificationService:EmailSender:BaseUrl"] = "http://mail.test";
					values["NotificationService:EmailSender:ApiKey"] = "k";
				}
				foreach (var (key, value) in more) values[key] = value;
				var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

				var services = new ServiceCollection();
				Database.Register(services);
				services.AddSingleton<TimeProvider>(Clock); // before the registration below, which only adds its own when there is none
				services.AddNotificationSender(configuration);
				if (emailOn) services.AddEmailDestinationResolver<FakeEmailDestinationResolver>();
				services.RemoveAll<IHostedService>();
				Provider = services.BuildServiceProvider();
			}

			public async ValueTask DisposeAsync()
			{
				await Provider.DisposeAsync();
				await Database.DisposeAsync();
			}

			/// <summary>Calls CreateEmail the way a host does: in a scope, saved by the caller, handed on when the scope is disposed.</summary>
			public async Task SendAsync(EmailMessage message, bool save = true)
			{
				using var scope = Provider.CreateScope();
				var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
				scope.ServiceProvider.GetRequiredService<INotificationSender>().CreateEmail(repository, message);
				if (save) await repository.SaveAsync();
			}

			public Task<List<OutboxMessage>> RowsAsync() => Database.QueryAsync(db => db.Set<OutboxMessage>().AsNoTracking().OrderBy(o => o.Id).ToListAsync());
		}

		private static EmailMessage valid() => new()
		{
			To = "sam.secret@example.com",
			ToName = "Sam Secret",
			Subject = "Subject-secret reset",
			Text = "text-secret-9f3a https://example.com/reset?token=secret-token",
			Html = "<p>html-secret-4c1d</p>",
			ReplyTo = "help.secret@example.com",
			ReplyToName = "Help Secret",
			Priority = MessagePriorities.High,
			Account = "support",
			Ref = "ref-secret-42",
		};

		[Fact]
		public async Task WritesExactlyOneOutboxRow_WithNoNotificationAndNoUser_AndTheEnvelopeReadyToSend()
		{
			await using var host = new Host();
			var message = valid();

			await host.SendAsync(message);

			var row = (await host.RowsAsync()).ShouldHaveSingleItem();
			row.NotificationId.ShouldBeNull();
			row.UserId.ShouldBeNull();
			row.Channel.ShouldBe(Channels.Email);
			row.State.ShouldBe(OutboxState.Pending);
			row.Account.ShouldBe("support");
			row.Ref.ShouldBe("ref-secret-42");
			row.CreatedAt.ShouldBe(host.Clock.GetUtcNow().UtcDateTime);
			row.ExpiresAt.ShouldBe(host.Clock.GetUtcNow().UtcDateTime.AddMinutes(30));
			row.MessageId.ShouldNotBe(Guid.Empty);
			row.Envelope.ShouldNotBeNullOrEmpty();

			var expected = new MessageEnvelope<EmailBody>
			{
				SchemaVersion = ContractVersion.Current,
				MessageId = row.MessageId,
				Channel = Channels.Email,
				Account = "support",
				Ref = "ref-secret-42",
				CreatedAt = new DateTimeOffset(row.CreatedAt, TimeSpan.Zero),
				ExpiresAt = new DateTimeOffset(row.ExpiresAt, TimeSpan.Zero),
				Body = new EmailBody
				{
					To = new EmailAddress { Address = "sam.secret@example.com", Name = "Sam Secret" },
					ReplyTo = new EmailAddress { Address = "help.secret@example.com", Name = "Help Secret" },
					Subject = "Subject-secret reset",
					Text = message.Text,
					Html = message.Html,
					Priority = MessagePriorities.High,
				},
			};
			ContractJson.Deserialize<MessageEnvelope<EmailBody>>(row.Envelope!).ShouldBe(expected);
		}

		[Fact]
		public async Task WritesNoNotificationRow_NoRecipientRow_AndNothingForThePushQueue()
		{
			await using var host = new Host();

			await host.SendAsync(valid());

			(await host.Database.QueryAsync(db => db.Set<Notification>().CountAsync())).ShouldBe(0);
			(await host.Database.QueryAsync(db => db.Set<NotificationToUser>().CountAsync())).ShouldBe(0);
			(await host.Database.QueryAsync(db => db.Set<FcmQueue>().CountAsync())).ShouldBe(0);
		}

		[Fact]
		public async Task TwoCalls_AreTwoRows_WithTheirOwnMessageIds()
		{
			await using var host = new Host();

			await host.SendAsync(valid());
			await host.SendAsync(valid());

			var rows = await host.RowsAsync();
			rows.Count.ShouldBe(2);
			rows[0].MessageId.ShouldNotBe(rows[1].MessageId);
		}

		[Fact]
		public async Task TheAccountIsTheMessagesOwn_ElseTheHostsDefaultForEmail_ElseNone()
		{
			await using var withDefault = new Host(true, ("NotificationService:EmailSender:DefaultAccount", "noreply"));
			await withDefault.SendAsync(valid() with { Account = "support" });
			await withDefault.SendAsync(valid() with { Account = null });
			var rows = await withDefault.RowsAsync();
			rows[0].Account.ShouldBe("support");
			rows[1].Account.ShouldBe("noreply");
			ContractJson.Deserialize<MessageEnvelope<EmailBody>>(rows[1].Envelope!)!.Account.ShouldBe("noreply");

			await using var without = new Host();
			await without.SendAsync(valid() with { Account = null });
			var none = (await without.RowsAsync()).Single();
			none.Account.ShouldBeNull(); // the email sender then uses this host's default account itself
			ContractJson.Deserialize<MessageEnvelope<EmailBody>>(none.Envelope!)!.Account.ShouldBeNull();
		}

		[Fact]
		public async Task TheMessageExpiresAfterTheConfiguredMinutes_LikeAnyOtherEmail()
		{
			await using var host = new Host(true, ("NotificationService:EmailSender:MessageExpirationMinutes", "90"));

			await host.SendAsync(valid());

			(await host.RowsAsync()).Single().ExpiresAt.ShouldBe(host.Clock.GetUtcNow().UtcDateTime.AddMinutes(90));
		}

		[Fact]
		public async Task ARowThatTheCallerNeverSaves_LeavesNothingBehind()
		{
			await using var host = new Host();

			await host.SendAsync(valid(), save: false); // the caller's save failed or its transaction rolled back

			(await host.RowsAsync()).ShouldBeEmpty();
		}

		[Fact]
		public async Task ANullMessage_IsRefused()
		{
			await using var host = new Host();

			await Should.ThrowAsync<ArgumentNullException>(() => host.SendAsync(null!));
		}

		// ---- validation: a case per rule ------------------------------------------------------------------

		public static TheoryData<string, EmailMessage> InvalidMessages()
		{
			var data = new TheoryData<string, EmailMessage>();
			void add(string field, EmailMessage message) => data.Add(field, message);

			add("To", valid() with { To = "" });
			add("To", valid() with { To = "   " });
			add("To", valid() with { To = "no-at-sign.example.com" });
			add("To", valid() with { To = "two@ats@example.com" });
			add("To", valid() with { To = "@nolocalpart.example.com" });
			add("To", valid() with { To = "nodomain@" });
			add("To", valid() with { To = "has space@example.com" });
			add("To", valid() with { To = "sam@example.com\r\nBcc: evil@example.com" });
			add("To", valid() with { To = "sam@example.com\n" });
			add("To", valid() with { To = "Sam Secret <sam@example.com>" });
			add("To", valid() with { To = "<sam@example.com>" });
			add("To", valid() with { To = "sam@example.com,pat@example.com" });
			add("To", valid() with { To = "sam@exam\tple.com" });
			add("ToName", valid() with { ToName = "Sam\r\nBcc: evil@example.com" });
			add("ToName", valid() with { ToName = "Sam\nSecret" });
			add("Subject", valid() with { Subject = "" });
			add("Subject", valid() with { Subject = "  " });
			add("Subject", valid() with { Subject = "line one\r\nBcc: evil@example.com" });
			add("Subject", valid() with { Subject = "line one\nline two" });
			add("Text and Html", valid() with { Text = null, Html = null });
			add("Text and Html", valid() with { Text = "  ", Html = "\r\n" });
			add("ReplyTo", valid() with { ReplyTo = "no-at-sign.example.com" });
			add("ReplyTo", valid() with { ReplyTo = "help@example.com,evil@example.com" });
			add("ReplyTo", valid() with { ReplyTo = "help@example.com\r\nBcc: evil@example.com" });
			add("ReplyTo", valid() with { ReplyTo = "Help Secret <help@example.com>" });
			add("ReplyTo", valid() with { ReplyTo = "" });
			add("ReplyToName", valid() with { ReplyToName = "Help\r\nBcc: evil@example.com" });
			add("ReplyToName", valid() with { ReplyTo = null, ReplyToName = "Help Secret" });
			add("Priority", valid() with { Priority = "urgent-secret" });
			add("Priority", valid() with { Priority = "highest-secret" });
			add("Priority", valid() with { Priority = "" });
			add("Ref", valid() with { Ref = new string('r', 257) });
			add("Account", valid() with { Account = "  " });
			add("Account", valid() with { Account = new string('a', 65) });
			return data;
		}

		[Theory]
		[MemberData(nameof(InvalidMessages))]
		public async Task AnInvalidMessage_IsRefusedAtTheCall_SayingWhichFieldIsWrongAndNeverWhatItHolds(string field, EmailMessage message)
		{
			await using var host = new Host();

			var failure = await Should.ThrowAsync<ArgumentException>(() => host.SendAsync(message));

			failure.GetType().ShouldBe(typeof(ArgumentException));
			failure.Message.ShouldContain($"EmailMessage.{field} ");
			// Not the address, the subject, the text, the link or the reference - not a single value of the message that was handed over.
			foreach (var value in new[] { message.To, message.ToName, message.Subject, message.Text, message.Html, message.ReplyTo, message.ReplyToName, message.Priority, message.Account, message.Ref })
			{
				if (!string.IsNullOrWhiteSpace(value) && value.Length >= 4) failure.Message.ShouldNotContain(value);
			}
			(await host.RowsAsync()).ShouldBeEmpty(); // nothing was added for the caller to save
		}

		[Theory]
		[InlineData(null)]
		[InlineData("normal")]
		[InlineData("high")]
		public async Task APriorityThatIsNullNormalOrHigh_IsAccepted(string? priority)
		{
			await using var host = new Host();

			await host.SendAsync(valid() with { Priority = priority });

			ContractJson.Deserialize<MessageEnvelope<EmailBody>>((await host.RowsAsync()).Single().Envelope!)!.Body.Priority.ShouldBe(priority);
		}

		[Fact]
		public async Task TheSmallestValidMessage_IsAccepted()
		{
			await using var host = new Host();

			await host.SendAsync(new EmailMessage { To = "sam@example.com", Subject = "Hi", Html = "<b>hi</b>" });

			var body = ContractJson.Deserialize<MessageEnvelope<EmailBody>>((await host.RowsAsync()).Single().Envelope!)!.Body;
			body.Text.ShouldBeNull();
			body.Html.ShouldBe("<b>hi</b>");
			body.ReplyTo.ShouldBeNull();
			body.To.Name.ShouldBeNull();
		}

		[Fact]
		public async Task ARefOfExactlyTheMaximumLength_IsAccepted()
		{
			await using var host = new Host();

			await host.SendAsync(valid() with { Ref = new string('r', 256) });

			(await host.RowsAsync()).Single().Ref!.Length.ShouldBe(256);
		}

		// ---- waking the relay ---------------------------------------------------------------------------

		[Fact]
		public async Task OnceTheCallerHasSavedAndTheScopeEnds_TheEmailRelayIsWokenAtOnce_AndNoOtherChannelIs()
		{
			await using var host = new Host();
			var signals = host.Provider.GetRequiredService<OutboxSignals>();
			signals.For(Channels.Email); // the relays create these when the host starts; with none running, do it up front
			signals.For(Channels.Fcm);

			await host.SendAsync(valid());

			var waited = Stopwatch.StartNew();
			await signals.For(Channels.Email).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
			waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "the email relay should have been woken, not left to its poll");

			waited.Restart();
			await signals.For(Channels.Fcm).WaitAsync(TimeSpan.FromMilliseconds(400), CancellationToken.None);
			waited.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(300), "a free-form email has nothing for the push relay");
		}

		[Fact]
		public async Task WhenTheCallerNeverSaved_NothingWakesTheRelay()
		{
			await using var host = new Host();
			var signal = host.Provider.GetRequiredService<OutboxSignals>().For(Channels.Email);

			await host.SendAsync(valid(), save: false);

			var waited = Stopwatch.StartNew();
			await signal.WaitAsync(TimeSpan.FromMilliseconds(400), CancellationToken.None);
			waited.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(300));
		}

		// ---- email off ------------------------------------------------------------------------------------

		[Fact]
		public async Task WithTheEmailTransportOff_ItThrows_NamingTheSetting_InsteadOfQueueingIntoTheVoid()
		{
			await using var host = new Host(emailOn: false);

			var failure = await Should.ThrowAsync<InvalidOperationException>(() => host.SendAsync(valid()));

			failure.Message.ShouldContain("NotificationService:EmailTransport");
			failure.Message.ShouldNotContain("sam.secret@example.com");
			(await host.RowsAsync()).ShouldBeEmpty();
		}

		[Fact]
		public async Task WithTheEmailTransportOff_ItIsTheSettingThatIsReported_EvenForAMessageThatIsAlsoInvalid()
		{
			await using var host = new Host(emailOn: false);

			await Should.ThrowAsync<InvalidOperationException>(() => host.SendAsync(valid() with { To = "not an address" }));
		}

		[Fact]
		public async Task WithPushSenderModeOnButEmailOff_ItStillThrows()
		{
			// OutboxSignals is registered here (for the push relay), so its presence cannot be what says email is on.
			await using var host = new Host(false,
				("NotificationService:FcmTransport", "Sender"),
				("NotificationService:FcmSender:BaseUrl", "http://push.test"),
				("NotificationService:FcmSender:ApiKey", "k"));

			var failure = await Should.ThrowAsync<InvalidOperationException>(() => host.SendAsync(valid()));

			failure.Message.ShouldContain("NotificationService:EmailTransport");
		}
	}
}
