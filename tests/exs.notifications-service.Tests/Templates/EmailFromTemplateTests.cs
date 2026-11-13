using exs.Database.Commons.Interfaces;
using exs.notifications_contracts;
using exs.notifications_model.Notifications;
using exs.notifications_model.Outbox;
using exs.notifications_model.Templates;
using exs.notifications_service.Interfaces;
using exs.notifications_service.Sender;
using exs.notifications_service.Templates;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace exs.notifications_service.Tests.Templates
{
	/// <summary>
	/// INotificationSender.CreateEmailFromTemplateAsync through the real registration: the template is filled in before
	/// anything is written, and what is written is exactly what CreateEmail would have written for the same message.
	/// </summary>
	public sealed class EmailFromTemplateTests : IAsyncDisposable
	{
		private readonly HostTestDatabase mDatabase = HostTestDatabase.Create();

		public ValueTask DisposeAsync() => mDatabase.DisposeAsync();

		private ServiceProvider BuildHost(bool emailOn = true, bool templatesOn = true)
		{
			var values = new Dictionary<string, string?>();
			if (emailOn)
			{
				values["NotificationService:EmailTransport"] = "Sender";
				values["NotificationService:EmailSender:BaseUrl"] = "http://mail.test";
				values["NotificationService:EmailSender:ApiKey"] = "k";
				values["NotificationService:EmailSender:DefaultAccount"] = "noreply";
			}
			var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

			var services = new ServiceCollection();
			mDatabase.Register(services);
			services.AddNotificationSender(configuration);
			if (emailOn) services.AddEmailDestinationResolver<FakeEmailDestinationResolver>();
			if (templatesOn) services.AddNotificationTemplates([typeof(CaseUpdatedData).Assembly], configuration);
			services.RemoveAll<IHostedService>();
			return services.BuildServiceProvider();
		}

		private async Task AddTemplateAsync(string subject, string? text, string? html = null, string language = "en", string key = "case-updated")
		{
			await using var context = mDatabase.CreateContext();
			context.Set<NotificationTemplate>().Add(new NotificationTemplate { TemplateKey = key, Language = language, Version = 1, IsActive = true, Subject = subject, Text = text, Html = html, CreatedAt = DateTime.UtcNow });
			await context.SaveChangesAsync();
		}

		private static CaseUpdatedData Model(string title = "Brake check") => new() { UserName = "Sam", Case = new CaseInfo { Title = title, User = new UserInfo { DisplayName = "Sam Rivera" } } };

		/// <summary>Calls it the way a host does: in a scope, saved by the caller.</summary>
		private static async Task SendAsync(ServiceProvider provider, EmailEnvelope email, CaseUpdatedData model, bool save = true, string? language = null)
		{
			using var scope = provider.CreateScope();
			var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
			await scope.ServiceProvider.GetRequiredService<INotificationSender>().CreateEmailFromTemplateAsync(repository, email, model, language);
			if (save) await repository.SaveAsync();
		}

		private Task<List<OutboxMessage>> RowsAsync() => mDatabase.QueryAsync(db => db.Set<OutboxMessage>().AsNoTracking().OrderBy(o => o.Id).ToListAsync());

		[Fact]
		public async Task WritesOneOutboxRow_WithTheTemplateFilledIn_AndNothingElse()
		{
			await AddTemplateAsync("Case {{ case.title }} updated", "Hi {{ user_name }}, code <secure>{{ code }}</secure>", "<p>Hi {{ user_name }}</p>");
			await using var provider = BuildHost();

			await SendAsync(provider, new EmailEnvelope { To = "sam@example.com", ToName = "Sam Rivera" }, Model());

			var row = (await RowsAsync()).ShouldHaveSingleItem();
			row.Channel.ShouldBe(Channels.Email);
			row.NotificationId.ShouldBeNull();
			row.UserId.ShouldBeNull();
			row.State.ShouldBe(OutboxState.Pending);
			row.Account.ShouldBe("noreply");
			var body = ContractJson.Deserialize<MessageEnvelope<EmailBody>>(row.Envelope!)!.Body;
			body.To.Address.ShouldBe("sam@example.com");
			body.To.Name.ShouldBe("Sam Rivera");
			body.Subject.ShouldBe("Case Brake check updated");
			// The tags are the sender's to strip and mask: the host passes them on untouched.
			body.Text.ShouldBe("Hi Sam, code <secure>123456</secure>");
			body.Html.ShouldBe("<p>Hi Sam</p>");
			(await mDatabase.QueryAsync(db => db.Set<Notification>().CountAsync())).ShouldBe(0);
		}

		[Fact]
		public async Task TheEnvelopeNamesTheTemplateKeyVersionAndLanguageThatWasUsed()
		{
			await AddTemplateAsync("S", "T");
			await using var provider = BuildHost();

			// Ukrainian was asked for, but only the default exists: the reference says what was actually used.
			await SendAsync(provider, new EmailEnvelope { To = "a@b.co" }, Model(), language: "uk");

			var envelope = ContractJson.Deserialize<MessageEnvelope<EmailBody>>((await RowsAsync()).Single().Envelope!)!;
			envelope.Template.ShouldBe(new TemplateReference { Key = "case-updated", Version = 1, Language = "en" });
		}

		[Fact]
		public async Task AHandWrittenEmailCarriesNoTemplate()
		{
			await using var provider = BuildHost();
			using (var scope = provider.CreateScope())
			{
				var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
				scope.ServiceProvider.GetRequiredService<INotificationSender>().CreateEmail(repository, new EmailMessage { To = "a@b.co", Subject = "S", Text = "T" });
				await repository.SaveAsync();
			}

			ContractJson.Deserialize<MessageEnvelope<EmailBody>>((await RowsAsync()).Single().Envelope!)!.Template.ShouldBeNull();
		}

		[Fact]
		public async Task TheDeliveryOptionsOfTheEmailArriveWithTheMessage()
		{
			await AddTemplateAsync("S", "T");
			await using var provider = BuildHost();

			await SendAsync(provider, new EmailEnvelope { To = "a@b.co", ReplyTo = "support@b.co", ReplyToName = "Support", Priority = MessagePriorities.High, Account = "billing", Ref = "case:7" }, Model());

			var row = (await RowsAsync()).Single();
			row.Account.ShouldBe("billing");
			row.Ref.ShouldBe("case:7");
			var body = ContractJson.Deserialize<MessageEnvelope<EmailBody>>(row.Envelope!)!.Body;
			body.ReplyTo!.Address.ShouldBe("support@b.co");
			body.ReplyTo.Name.ShouldBe("Support");
			body.Priority.ShouldBe(MessagePriorities.High);
		}

		[Fact]
		public async Task TheWantedLanguageIsUsed()
		{
			await AddTemplateAsync("english", "T");
			await AddTemplateAsync("ukrainian", "T", language: "uk");
			await using var provider = BuildHost();

			await SendAsync(provider, new EmailEnvelope { To = "a@b.co" }, Model(), language: "uk-UA");

			ContractJson.Deserialize<MessageEnvelope<EmailBody>>((await RowsAsync()).Single().Envelope!)!.Body.Subject.ShouldBe("ukrainian");
		}

		[Fact]
		public async Task ANotSavedCallLeavesNothingBehind()
		{
			await AddTemplateAsync("S", "T");
			await using var provider = BuildHost();

			await SendAsync(provider, new EmailEnvelope { To = "a@b.co" }, Model(), save: false);

			(await RowsAsync()).ShouldBeEmpty();
		}

		[Fact]
		public async Task AMissingTemplateFailsTheCall_AndWritesNothing()
		{
			await using var provider = BuildHost();

			var ex = await Should.ThrowAsync<TemplateNotFoundException>(() => SendAsync(provider, new EmailEnvelope { To = "a@b.co" }, Model()));

			ex.TemplateKey.ShouldBe("case-updated");
			(await RowsAsync()).ShouldBeEmpty();
		}

		[Fact]
		public async Task AModelWithAMissingValueFailsTheCall_AndWritesNothing()
		{
			await AddTemplateAsync("S", "T");
			await using var provider = BuildHost();
			var broken = new CaseUpdatedData { UserName = "Sam", Case = null! };

			var ex = await Should.ThrowAsync<TemplateModelException>(() => SendAsync(provider, new EmailEnvelope { To = "a@b.co" }, broken));

			ex.MissingPaths.ShouldBe(["Case"]);
			(await RowsAsync()).ShouldBeEmpty();
		}

		[Fact]
		public async Task AValueThatPutsALineBreakInTheSubject_IsRefused_NotSent()
		{
			await AddTemplateAsync("Case {{ case.title }}", "T");
			await using var provider = BuildHost();

			await Should.ThrowAsync<ArgumentException>(() => SendAsync(provider, new EmailEnvelope { To = "a@b.co" }, Model("one\r\nBcc: evil@x.co")));

			(await RowsAsync()).ShouldBeEmpty();
		}

		[Fact]
		public async Task AnInvalidAddressIsRefused_ByTheSameChecksAsAHandWrittenEmail()
		{
			await AddTemplateAsync("S", "T");
			await using var provider = BuildHost();

			await Should.ThrowAsync<ArgumentException>(() => SendAsync(provider, new EmailEnvelope { To = "not an address" }, Model()));

			(await RowsAsync()).ShouldBeEmpty();
		}

		[Fact]
		public async Task WithTheEmailTransportOff_ItSaysSo()
		{
			await using var provider = BuildHost(emailOn: false);

			var ex = await Should.ThrowAsync<InvalidOperationException>(() => SendAsync(provider, new EmailEnvelope { To = "a@b.co" }, Model()));

			ex.Message.ShouldContain("EmailTransport");
		}

		[Fact]
		public async Task WithoutAddNotificationTemplates_ItSaysSo()
		{
			await using var provider = BuildHost(templatesOn: false);

			var ex = await Should.ThrowAsync<InvalidOperationException>(() => SendAsync(provider, new EmailEnvelope { To = "a@b.co" }, Model()));

			ex.Message.ShouldContain("AddNotificationTemplates");
		}
	}
}
