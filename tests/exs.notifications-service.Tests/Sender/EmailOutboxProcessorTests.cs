using exs.notifications_contracts;
using exs.notifications_model.Notifications;
using exs.notifications_model.Outbox;
using exs.notifications_service.Sender;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.notifications_service.Tests.Sender
{
	public sealed class EmailOutboxProcessorTests : IAsyncDisposable
	{
		private readonly HostTestDatabase mDatabase = HostTestDatabase.Create();
		private readonly FakeEmailDestinationResolver mResolver = new();
		private readonly ManualTimeProvider mClock = new();

		public ValueTask DisposeAsync() => mDatabase.DisposeAsync();

		private EmailOutboxProcessor create(Action<SenderTransportOptions>? configure = null, IEmailContentFactory? content = null)
		{
			var options = new SenderTransportOptions();
			configure?.Invoke(options);
			var services = new ServiceCollection().AddSingleton<IEmailDestinationResolver>(mResolver);
			if (content != null) services.AddSingleton(content);
			return new EmailOutboxProcessor(services.BuildServiceProvider(), Options.Create(options), mClock, NullLogger<EmailOutboxProcessor>.Instance);
		}

		/// <summary>Runs the processor the way NotificationService does: against a repository, then one save.</summary>
		private async Task processAsync(EmailOutboxProcessor processor, int notificationId, params int[] userIds)
		{
			using var repository = mDatabase.CreateRepository();
			await processor.ProcessNotificationAsync(repository, notificationId, userIds.ToList(), CancellationToken.None);
			await repository.SaveAsync();
		}

		private Task<List<OutboxMessage>> rowsAsync() => mDatabase.QueryAsync(db => db.Set<OutboxMessage>().AsNoTracking().OrderBy(o => o.Id).ToListAsync());

		[Fact]
		public void ServesTheEmailTransport()
		{
			create().TransportType.ShouldBe(NotificationTransportType.Email);
		}

		[Fact]
		public async Task WithoutAResolverRegistered_ItFailsWithAMessageSayingWhatToCall_AndWritesNothing()
		{
			var notification = await mDatabase.AddNotificationAsync();
			var processor = new EmailOutboxProcessor(new ServiceCollection().BuildServiceProvider(), Options.Create(new SenderTransportOptions()),
				mClock, NullLogger<EmailOutboxProcessor>.Instance);

			var failure = await Should.ThrowAsync<InvalidOperationException>(() => processAsync(processor, notification.Id, 1));

			failure.Message.ShouldContain(nameof(IEmailDestinationResolver));
			failure.Message.ShouldContain("AddEmailDestinationResolver");
			(await rowsAsync()).ShouldBeEmpty();
		}

		[Fact]
		public async Task WritesOnePendingRowPerAddress_WithTheEnvelopeReadyToSend()
		{
			var notification = await mDatabase.AddNotificationAsync(title: "Search Party Joined", body: "Sam joined", typeId: 7);
			mResolver.Destinations.Add(new EmailResolvedDestination(1, "sam@example.com", "Sam Rivera", "noreply", "u:1"));

			await processAsync(create(), notification.Id, 1);

			var row = (await rowsAsync()).ShouldHaveSingleItem();
			row.State.ShouldBe(OutboxState.Pending);
			row.Channel.ShouldBe("email");
			row.NotificationId.ShouldBe(notification.Id);
			row.UserId.ShouldBe(1);
			row.Account.ShouldBe("noreply");
			row.Ref.ShouldBe("u:1");
			row.CreatedAt.ShouldBe(mClock.GetUtcNow().UtcDateTime);
			row.ExpiresAt.ShouldBe(mClock.GetUtcNow().UtcDateTime.AddMinutes(30));

			var envelope = ContractJson.Deserialize<MessageEnvelope<EmailBody>>(row.Envelope!)!;
			envelope.MessageId.ShouldBe(row.MessageId);
			envelope.SchemaVersion.ShouldBe(ContractVersion.Current);
			envelope.Channel.ShouldBe("email");
			envelope.Account.ShouldBe("noreply");
			envelope.Ref.ShouldBe("u:1");
			envelope.Body.To.Address.ShouldBe("sam@example.com");
			envelope.Body.To.Name.ShouldBe("Sam Rivera");
			envelope.Body.Subject.ShouldBe("Search Party Joined");
			envelope.Body.Text.ShouldBe("Sam joined");
			envelope.Body.Html.ShouldBeNull();
			envelope.Body.ReplyTo.ShouldBeNull();
		}

		[Fact]
		public async Task TheMessageExpiresAfterTheConfiguredMinutes()
		{
			var notification = await mDatabase.AddNotificationAsync();
			mResolver.Destinations.Add(new EmailResolvedDestination(1, "sam@example.com"));

			await processAsync(create(o => o.MessageExpirationMinutes = 90), notification.Id, 1);

			(await rowsAsync()).Single().ExpiresAt.ShouldBe(mClock.GetUtcNow().UtcDateTime.AddMinutes(90));
		}

		[Fact]
		public async Task TheAccountIsTheDestinationsOwn_ElseTheHostDefault_ElseNone()
		{
			var notification = await mDatabase.AddNotificationAsync();
			mResolver.Destinations.Add(new EmailResolvedDestination(1, "a@example.com", Account: "support"));
			mResolver.Destinations.Add(new EmailResolvedDestination(2, "b@example.com"));

			await processAsync(create(o => o.DefaultAccount = "noreply"), notification.Id, 1, 2);
			var withDefault = await rowsAsync();
			withDefault.Single(r => r.UserId == 1).Account.ShouldBe("support");
			withDefault.Single(r => r.UserId == 2).Account.ShouldBe("noreply");
			ContractJson.Deserialize<MessageEnvelope<EmailBody>>(withDefault.Single(r => r.UserId == 2).Envelope!)!.Account.ShouldBe("noreply");

			var other = await mDatabase.AddNotificationAsync();
			await processAsync(create(), other.Id, 2);
			(await rowsAsync()).Single(r => r.NotificationId == other.Id).Account.ShouldBeNull();
		}

		[Fact]
		public async Task AUserWithNoAddress_GetsAFinishedRow_SoTheLogAnswersForEveryone()
		{
			var notification = await mDatabase.AddNotificationAsync();
			mResolver.Destinations.Add(new EmailResolvedDestination(1, "sam@example.com"));

			await processAsync(create(), notification.Id, 1, 2);

			var none = (await rowsAsync()).Single(r => r.UserId == 2);
			none.State.ShouldBe(OutboxState.Completed);
			none.Channel.ShouldBe("email");
			none.ResultStatus.ShouldBe(ResultStatuses.Failed);
			none.ErrorKind.ShouldBe(OutboxErrorKind.NoDestination);
			none.ErrorCode.ShouldBe(OutboxErrorCode.NoEmailAddress);
			none.Envelope.ShouldBeNull();
			none.CompletedAt.ShouldNotBeNull();
		}

		[Fact]
		public async Task ANotificationWithNoEmailForm_IsRecordedAsSuch_NotSent()
		{
			// A data-only notification has no title, so the default content factory has no subject to give.
			var notification = await mDatabase.AddNotificationAsync(title: null, body: null, payload: """{ "k": "v" }""");
			mResolver.Destinations.Add(new EmailResolvedDestination(1, "sam@example.com", Ref: "u:1"));

			await processAsync(create(), notification.Id, 1);

			var row = (await rowsAsync()).ShouldHaveSingleItem();
			row.State.ShouldBe(OutboxState.Completed);
			row.ErrorKind.ShouldBe(OutboxErrorKind.NoContent);
			row.ErrorCode.ShouldBe(OutboxErrorCode.NoEmailContent);
			row.Ref.ShouldBe("u:1");
			row.Envelope.ShouldBeNull();
		}

		[Fact]
		public async Task AHostsOwnContentFactory_DecidesTheMail()
		{
			var notification = await mDatabase.AddNotificationAsync(title: "T", body: "B", typeId: 3);
			mResolver.Destinations.Add(new EmailResolvedDestination(1, "sam@example.com", "Sam"));
			var factory = new FakeEmailContentFactory
			{
				OnBuild = (n, d) => new EmailContent($"[{n.TypeId}] {d.Name}", "plain", "<b>html</b>", ReplyTo: "help@example.com", Priority: MessagePriorities.High),
			};

			await processAsync(create(content: factory), notification.Id, 1);

			var body = ContractJson.Deserialize<MessageEnvelope<EmailBody>>((await rowsAsync()).Single().Envelope!)!.Body;
			body.Subject.ShouldBe("[3] Sam");
			body.Text.ShouldBe("plain");
			body.Html.ShouldBe("<b>html</b>");
			body.ReplyTo!.Address.ShouldBe("help@example.com");
			body.Priority.ShouldBe(MessagePriorities.High);
		}

		[Fact]
		public async Task TheDefaultContent_IsTheTitleAsSubject_AndTheBodyAsText()
		{
			var notification = await mDatabase.AddNotificationAsync(title: "  Hello  ", body: "World");
			mResolver.Destinations.Add(new EmailResolvedDestination(1, "sam@example.com"));

			await processAsync(create(), notification.Id, 1);

			var body = ContractJson.Deserialize<MessageEnvelope<EmailBody>>((await rowsAsync()).Single().Envelope!)!.Body;
			body.Subject.ShouldBe("Hello");
			body.Text.ShouldBe("World");
		}

		[Fact]
		public async Task ATitleOnlyNotification_GetsTheTitleAsItsText_BecauseAMailNeedsContent()
		{
			var notification = await mDatabase.AddNotificationAsync(title: "Only a title", body: null);
			mResolver.Destinations.Add(new EmailResolvedDestination(1, "sam@example.com"));

			await processAsync(create(), notification.Id, 1);

			ContractJson.Deserialize<MessageEnvelope<EmailBody>>((await rowsAsync()).Single().Envelope!)!.Body.Text.ShouldBe("Only a title");
		}

		[Fact]
		public async Task RunningTwiceForTheSameNotification_DoesNotEmailAnyoneTwice()
		{
			var notification = await mDatabase.AddNotificationAsync();
			mResolver.Destinations.Add(new EmailResolvedDestination(1, "sam@example.com"));

			await processAsync(create(), notification.Id, 1);
			await processAsync(create(), notification.Id, 1);

			(await rowsAsync()).Count.ShouldBe(1);
		}

		[Fact]
		public async Task ThePushRowsOfTheSameNotification_DoNotCountAsEmailsAlreadyWritten()
		{
			var notification = await mDatabase.AddNotificationAsync();
			await using (var context = mDatabase.CreateContext())
			{
				context.Set<OutboxMessage>().Add(new OutboxMessage
				{
					MessageId = Guid.NewGuid(), NotificationId = notification.Id, UserId = 1, Channel = "fcm",
					CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(30), State = OutboxState.Pending,
				});
				await context.SaveChangesAsync();
			}
			mResolver.Destinations.Add(new EmailResolvedDestination(1, "sam@example.com"));

			await processAsync(create(), notification.Id, 1);

			(await rowsAsync()).Count(r => r.Channel == "email").ShouldBe(1);
		}

		[Fact]
		public async Task AUserListedTwice_IsEmailedOnce()
		{
			var notification = await mDatabase.AddNotificationAsync();
			mResolver.Destinations.Add(new EmailResolvedDestination(1, "sam@example.com"));

			await processAsync(create(), notification.Id, 1, 1, 1);

			(await rowsAsync()).Count.ShouldBe(1);
		}

		[Fact]
		public async Task AResolverThatThrows_WritesNothing()
		{
			var notification = await mDatabase.AddNotificationAsync();
			mResolver.Throws = new InvalidOperationException("db down");

			await Should.ThrowAsync<InvalidOperationException>(() => processAsync(create(), notification.Id, 1));

			(await rowsAsync()).ShouldBeEmpty();
		}

		[Fact]
		public async Task ANotificationThatDoesNotExist_Fails()
		{
			await Should.ThrowAsync<InvalidOperationException>(() => processAsync(create(), 999, 1));
		}
	}
}
