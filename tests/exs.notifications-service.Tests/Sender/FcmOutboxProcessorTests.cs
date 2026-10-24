using exs.notifications_contracts;
using exs.notifications_model.Notifications;
using exs.notifications_model.Outbox;
using exs.notifications_model.Transports;
using exs.notifications_service.Sender;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.notifications_service.Tests.Sender
{
	public sealed class FcmOutboxProcessorTests : IAsyncDisposable
	{
		private readonly HostTestDatabase mDatabase = HostTestDatabase.Create();
		private readonly FakeFcmDestinationResolver mResolver = new();
		private readonly ManualTimeProvider mClock = new();

		public ValueTask DisposeAsync() => mDatabase.DisposeAsync();

		private FcmOutboxProcessor create(Action<SenderTransportOptions>? configure = null)
		{
			var options = new SenderTransportOptions();
			configure?.Invoke(options);
			var hooks = new ServiceCollection().AddSingleton<IFcmDestinationResolver>(mResolver).BuildServiceProvider();
			return new FcmOutboxProcessor(hooks, Options.Create(options), mClock, NullLogger<FcmOutboxProcessor>.Instance);
		}

		[Fact]
		public async Task WithoutAResolverRegistered_ItFailsWithAMessageSayingWhatToCall_AndWritesNothing()
		{
			var notification = await mDatabase.AddNotificationAsync();
			var noHooks = new ServiceCollection().BuildServiceProvider();
			var processor = new FcmOutboxProcessor(noHooks, Options.Create(new SenderTransportOptions()), mClock, NullLogger<FcmOutboxProcessor>.Instance);

			var failure = await Should.ThrowAsync<InvalidOperationException>(() => processAsync(processor, notification.Id, 1));

			failure.Message.ShouldContain(nameof(IFcmDestinationResolver));
			failure.Message.ShouldContain("AddUserSessionFcmHooks");
			(await rowsAsync()).ShouldBeEmpty();
		}

		/// <summary>Runs the processor the way NotificationService does: against a repository, then one save.</summary>
		private async Task processAsync(FcmOutboxProcessor processor, int notificationId, params int[] userIds)
		{
			using var repository = mDatabase.CreateRepository();
			await processor.ProcessNotificationAsync(repository, notificationId, userIds.ToList(), CancellationToken.None);
			await repository.SaveAsync();
		}

		private Task<List<OutboxMessage>> rowsAsync() => mDatabase.QueryAsync(db => db.Set<OutboxMessage>().AsNoTracking().OrderBy(o => o.Id).ToListAsync());

		[Fact]
		public async Task TheEmailRowsOfTheSameNotification_DoNotCountAsPushesAlreadyWritten()
		{
			var notification = await mDatabase.AddNotificationAsync();
			await using (var context = mDatabase.CreateContext())
			{
				context.Set<OutboxMessage>().Add(new OutboxMessage
				{
					MessageId = Guid.NewGuid(), NotificationId = notification.Id, UserId = 1, Channel = "email",
					CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(30), State = OutboxState.Pending,
				});
				await context.SaveChangesAsync();
			}
			mResolver.Destinations.Add(new FcmResolvedDestination(1, "tok-a", FcmPlatforms.Android));

			await processAsync(create(), notification.Id, 1);

			(await rowsAsync()).Count(r => r.Channel == "fcm").ShouldBe(1);
		}

		[Fact]
		public void ServesTheFcmTransport()
		{
			create().TransportType.ShouldBe(NotificationTransportType.FCM);
		}

		[Fact]
		public async Task WritesOnePendingRowPerDestination_WithTheEnvelopeReadyToSend()
		{
			var notification = await mDatabase.AddNotificationAsync(title: "Hello", body: "World", payload: """{ "k": "v" }""", typeId: 7, entityId: 55);
			mResolver.Destinations.Add(new FcmResolvedDestination(1, "tok-a", FcmPlatforms.Android, "mf5", "s:10"));
			mResolver.Destinations.Add(new FcmResolvedDestination(1, "tok-b", FcmPlatforms.Ios, "mf5", "s:11"));

			await processAsync(create(), notification.Id, 1);

			var rows = await rowsAsync();
			rows.Count.ShouldBe(2);
			rows.ShouldAllBe(r => r.State == OutboxState.Pending && r.NotificationId == notification.Id && r.UserId == 1 && r.Channel == "fcm");
			rows.Select(r => r.MessageId).Distinct().Count().ShouldBe(2);

			var first = rows[0];
			first.Account.ShouldBe("mf5");
			first.Ref.ShouldBe("s:10");
			var envelope = ContractJson.Deserialize<MessageEnvelope<FcmBody>>(first.Envelope!)!;
			envelope.MessageId.ShouldBe(first.MessageId);
			envelope.SchemaVersion.ShouldBe(ContractVersion.Current);
			envelope.Channel.ShouldBe("fcm");
			envelope.Account.ShouldBe("mf5");
			envelope.Ref.ShouldBe("s:10");
			envelope.Body.Destination.Token.ShouldBe("tok-a");
			envelope.Body.Destination.Platform.ShouldBe(FcmPlatforms.Android);
			envelope.Body.Title.ShouldBe("Hello");
			envelope.Body.Body.ShouldBe("World");
			envelope.Body.Data!["k"].ShouldBe("v");
			envelope.Body.Data["TypeId"].ShouldBe("7");
			envelope.Body.Data["EntityId"].ShouldBe("55");
		}

		[Fact]
		public async Task TheDeadlineIsFromNow_ByTheConfiguredMinutes_OnBothTheRowAndTheEnvelope()
		{
			var notification = await mDatabase.AddNotificationAsync();
			mResolver.Destinations.Add(new FcmResolvedDestination(1, "tok", FcmPlatforms.Android));

			await processAsync(create(o => o.MessageExpirationMinutes = 10), notification.Id, 1);

			var row = (await rowsAsync()).Single();
			var now = mClock.GetUtcNow().UtcDateTime;
			row.CreatedAt.ShouldBe(now);
			row.ExpiresAt.ShouldBe(now.AddMinutes(10));
			var envelope = ContractJson.Deserialize<MessageEnvelope<FcmBody>>(row.Envelope!)!;
			envelope.CreatedAt.UtcDateTime.ShouldBe(now);
			envelope.ExpiresAt.UtcDateTime.ShouldBe(now.AddMinutes(10));
		}

		[Fact]
		public async Task AUserWithNoDestination_GetsAnAlreadyFinishedRow_SoTheLogStillAnswersForThem()
		{
			var notification = await mDatabase.AddNotificationAsync();
			mResolver.Destinations.Add(new FcmResolvedDestination(1, "tok", FcmPlatforms.Android));

			await processAsync(create(), notification.Id, 1, 2);

			var rows = await rowsAsync();
			rows.Count.ShouldBe(2);
			var none = rows.Single(r => r.UserId == 2);
			none.State.ShouldBe(OutboxState.Completed);
			none.ResultStatus.ShouldBe(ResultStatuses.Failed);
			none.ErrorKind.ShouldBe(OutboxErrorKind.NoDestination);
			none.ErrorCode.ShouldBe(FcmSentErrorCode.NoActiveSessions);
			none.CompletedAt.ShouldBe(mClock.GetUtcNow().UtcDateTime);
			none.Envelope.ShouldBeNull(); // nothing to submit, so the relay never sees it
			rows.Single(r => r.UserId == 1).State.ShouldBe(OutboxState.Pending);
		}

		[Fact]
		public async Task RunningTwiceForTheSameNotification_DoesNotQueueItAgain()
		{
			// The sweep re-fans-out a notification whose status was not flipped, e.g. after a crash between
			// the save and the flip. Without this every device would be pushed twice.
			var notification = await mDatabase.AddNotificationAsync();
			mResolver.Destinations.Add(new FcmResolvedDestination(1, "tok", FcmPlatforms.Android));
			var processor = create();

			await processAsync(processor, notification.Id, 1, 2);
			await processAsync(processor, notification.Id, 1, 2);

			(await rowsAsync()).Count.ShouldBe(2); // one real, one no-destination: not four
		}

		[Fact]
		public async Task OnlyTheUsersNotYetDone_AreProcessedOnARepeat()
		{
			var notification = await mDatabase.AddNotificationAsync();
			mResolver.Destinations.Add(new FcmResolvedDestination(1, "tok-1", FcmPlatforms.Android));
			mResolver.Destinations.Add(new FcmResolvedDestination(2, "tok-2", FcmPlatforms.Android));
			var processor = create();

			await processAsync(processor, notification.Id, 1);
			await processAsync(processor, notification.Id, 1, 2);

			var rows = await rowsAsync();
			rows.Count.ShouldBe(2);
			rows.Select(r => r.UserId).ShouldBe([1, 2], ignoreOrder: true);
		}

		[Fact]
		public async Task DuplicateUserIds_AreOneUser()
		{
			var notification = await mDatabase.AddNotificationAsync();
			mResolver.Destinations.Add(new FcmResolvedDestination(1, "tok", FcmPlatforms.Android));

			await processAsync(create(), notification.Id, 1, 1, 1);

			(await rowsAsync()).Count.ShouldBe(1);
		}

		[Fact]
		public async Task NoUsers_DoesNothing_AndDoesNotEvenAskTheResolver()
		{
			var notification = await mDatabase.AddNotificationAsync();

			await processAsync(create(), notification.Id);

			(await rowsAsync()).ShouldBeEmpty();
			mResolver.Calls.ShouldBe(0);
		}

		[Fact]
		public async Task LeavesTheNotificationStatusToTheFanOut()
		{
			var notification = await mDatabase.AddNotificationAsync();
			mResolver.Destinations.Add(new FcmResolvedDestination(1, "tok", FcmPlatforms.Android));

			await processAsync(create(), notification.Id, 1);

			(await mDatabase.QueryAsync(db => db.Set<Notification>().SingleAsync())).Status.ShouldBe(NotificationStatus.New);
		}

		[Fact]
		public async Task ANotificationThatDoesNotExist_IsAnError_AndWritesNothing()
		{
			mResolver.Destinations.Add(new FcmResolvedDestination(1, "tok", FcmPlatforms.Android));

			await Should.ThrowAsync<InvalidOperationException>(() => processAsync(create(), 12345, 1));

			(await rowsAsync()).ShouldBeEmpty();
		}

		[Fact]
		public async Task AResolverThatFails_LeavesNothingBehindToBeSavedWithTheNextNotification()
		{
			var notification = await mDatabase.AddNotificationAsync();
			mResolver.Throws = new InvalidOperationException("the session query failed");
			using var repository = mDatabase.CreateRepository(); // one repository for the batch, as NotificationService uses

			await Should.ThrowAsync<InvalidOperationException>(() =>
				create().ProcessNotificationAsync(repository, notification.Id, [1], CancellationToken.None));
			await repository.SaveAsync();

			(await rowsAsync()).ShouldBeEmpty();
		}
	}
}
