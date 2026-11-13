using System.Security.Cryptography;
using exs.Database.Commons.Impl;
using exs.notifications_contracts;
using exs.notifications_database;
using exs.notifications_model.Outbox;
using exs.notifications_service.Interfaces;
using exs.notifications_service.Sender;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.notifications_service.Tests.Sender
{
	/// <summary>
	/// The envelope of a queued message (an address, a reset link) is encrypted in the host's outbox while the row waits,
	/// so a copy of the database does not show it if the publisher stops before the sender has the message.
	/// </summary>
	public sealed class OutboxEncryptionTests : IAsyncDisposable
	{
		private static PayloadCipher NewCipher(string id = "k1") =>
			PayloadCipher.Create(new PayloadCipherOptions { CurrentKeyId = id, Keys = { [id] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) } });

		private readonly HostTestDatabase mDatabase = HostTestDatabase.Create();
		private readonly FakeNotificationTransport mTransport = new();
		private readonly ManualTimeProvider mClock = new();

		public ValueTask DisposeAsync() => mDatabase.DisposeAsync();

		private async Task<OutboxMessage> WriteFreeFormAsync(PayloadCipher? cipher)
		{
			var options = new ServiceCollection().Configure<SenderTransportOptions>("email", o => { o.BaseUrl = "http://mail.test"; o.ApiKey = "k"; }).BuildServiceProvider()
				.GetRequiredService<IOptionsMonitor<SenderTransportOptions>>();
			await using var context = mDatabase.CreateContext();
			var row = new EmailOutboxWriter(options, mClock, cipher).Write(new EntityFrameworkRepository<NotificationDatabaseContext>(context),
				new EmailMessage { To = "reset@example.com", Subject = "Reset", Text = "https://example.com/reset?token=abc" });
			await context.SaveChangesAsync();
			return row;
		}

		private OutboxRelay Relay(PayloadCipher? cipher)
		{
			var options = new SenderTransportOptions { BaseUrl = "http://mail.test", ApiKey = "k", RelayPollIntervalSeconds = 60, RelayMaxConcurrency = 2 };
			var services = new ServiceCollection();
			mDatabase.Register(services);
			var provider = services.BuildServiceProvider();
			return new OutboxRelay(provider.GetRequiredService<IServiceScopeFactory>(), mTransport, Options.Create(options), new OutboxSignal(), mClock, NullLogger<OutboxRelay>.Instance, Channels.Email, cipher);
		}

		private Task<OutboxMessage> RowAsync(long id) => mDatabase.QueryAsync(db => db.Set<OutboxMessage>().AsNoTracking().SingleAsync(o => o.Id == id));

		[Fact]
		public async Task TheEnvelopeIsEncryptedWhileTheRowWaits()
		{
			var cipher = NewCipher();

			var row = await WriteFreeFormAsync(cipher);

			var stored = await RowAsync(row.Id);
			stored.Envelope!.ShouldStartWith(PayloadCipher.Prefix);
			stored.Envelope.ShouldNotContain("reset@example.com");
			stored.Envelope.ShouldNotContain("token=abc");
			cipher.Unprotect(stored.Envelope, PayloadCipher.ContextFor(stored.MessageId, stored.Channel)).ShouldContain("reset@example.com");
		}

		[Fact]
		public async Task TheRelaySubmitsTheEnvelopeInTheClear_AndClearsItOnceAccepted()
		{
			var cipher = NewCipher();
			var row = await WriteFreeFormAsync(cipher);

			(await Relay(cipher).SubmitOneAsync(row.Id, CancellationToken.None)).ShouldBe(RelayOutcome.Submitted);

			var submitted = mTransport.Submitted.ShouldHaveSingleItem();
			submitted.ShouldContain("reset@example.com");
			submitted.ShouldNotStartWith(PayloadCipher.Prefix);
			ContractJson.Deserialize<MessageEnvelope<EmailBody>>(submitted)!.MessageId.ShouldBe(row.MessageId);
			(await RowAsync(row.Id)).Envelope.ShouldBeNull();
		}

		[Fact]
		public async Task WithoutAKey_EverythingWorksAsBefore()
		{
			var row = await WriteFreeFormAsync(null);
			(await RowAsync(row.Id)).Envelope!.ShouldContain("reset@example.com");

			(await Relay(null).SubmitOneAsync(row.Id, CancellationToken.None)).ShouldBe(RelayOutcome.Submitted);
		}

		[Fact]
		public async Task ARowWrittenBeforeEncryptionWasSwitchedOn_IsStillSubmitted()
		{
			var row = await WriteFreeFormAsync(null);

			(await Relay(NewCipher()).SubmitOneAsync(row.Id, CancellationToken.None)).ShouldBe(RelayOutcome.Submitted);

			mTransport.Submitted.ShouldHaveSingleItem().ShouldContain("reset@example.com");
		}

		[Fact]
		public async Task ARowWhoseKeyIsGone_StaysPending_AndIsNotSubmitted()
		{
			var row = await WriteFreeFormAsync(NewCipher("old"));

			(await Relay(NewCipher("new")).SubmitOneAsync(row.Id, CancellationToken.None)).ShouldBe(RelayOutcome.TransportFailure);

			mTransport.Submitted.ShouldBeEmpty();
			var stored = await RowAsync(row.Id);
			stored.State.ShouldBe(OutboxState.Pending);
			stored.Envelope.ShouldNotBeNull();
			stored.SubmitError.ShouldNotBeNull();
			stored.SubmitError.ShouldNotContain("reset@example.com");
		}

		[Fact]
		public async Task AnEnvelopeCopiedOntoAnotherRowCannotBeSubmitted()
		{
			var cipher = NewCipher();
			var first = await WriteFreeFormAsync(cipher);
			var second = await WriteFreeFormAsync(cipher);
			await mDatabase.QueryAsync(async db =>
			{
				var target = await db.Set<OutboxMessage>().SingleAsync(o => o.Id == second.Id);
				target.Envelope = (await RowAsync(first.Id)).Envelope;
				await db.SaveChangesAsync();
				return 0;
			});

			(await Relay(cipher).SubmitOneAsync(second.Id, CancellationToken.None)).ShouldBe(RelayOutcome.TransportFailure);

			mTransport.Submitted.ShouldBeEmpty();
		}

		[Fact]
		public void TheHostRegistersTheCipherFromItsConfiguration()
		{
			var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
			var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
			{
				["NotificationService:Encryption:CurrentKeyId"] = "k1",
				["NotificationService:Encryption:Keys:k1"] = key,
			}).Build();
			var services = new ServiceCollection();
			mDatabase.Register(services);
			services.AddNotificationSender(configuration);
			using var provider = services.BuildServiceProvider();

			provider.GetRequiredService<PayloadCipher>().IsEnabled.ShouldBeTrue();
		}

		[Fact]
		public void ABadKeyStopsTheHostAtRegistration()
		{
			var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
			{
				["NotificationService:Encryption:CurrentKeyId"] = "k1",
				["NotificationService:Encryption:Keys:k1"] = "short",
			}).Build();

			Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddNotificationSender(configuration));
		}

		[Fact]
		public void WithNoConfiguration_NothingIsEncrypted()
		{
			var services = new ServiceCollection();
			mDatabase.Register(services);
			services.AddNotificationSender();
			using var provider = services.BuildServiceProvider();

			provider.GetRequiredService<PayloadCipher>().IsEnabled.ShouldBeFalse();
		}
	}
}
