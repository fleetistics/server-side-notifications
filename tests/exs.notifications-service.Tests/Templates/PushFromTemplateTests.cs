using exs.Database.Commons.Interfaces;
using exs.notifications_model.Notifications;
using exs.notifications_model.Outbox;
using exs.notifications_model.Templates;
using exs.notifications_service.Interfaces;
using exs.notifications_service.Templates;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace exs.notifications_service.Tests.Templates
{
	/// <summary>
	/// INotificationSender.CreateMobileNotificationFromTemplateAsync through the real registration: the template's Subject and
	/// Text become the push title and body, in each recipient's own language, one notification row per language.
	/// </summary>
	public sealed class PushFromTemplateTests : IAsyncDisposable
	{
		private readonly HostTestDatabase mDatabase = HostTestDatabase.Create();

		public ValueTask DisposeAsync() => mDatabase.DisposeAsync();

		private ServiceProvider BuildHost(bool templatesOn = true)
		{
			var services = new ServiceCollection();
			mDatabase.Register(services);
			services.AddNotificationSender();
			if (templatesOn) services.AddNotificationTemplates([typeof(CaseUpdatedData).Assembly]);
			services.RemoveAll<IHostedService>();
			return services.BuildServiceProvider();
		}

		private async Task AddTemplateAsync(string subject, string? text, string language = "en", string? html = null)
		{
			await using var context = mDatabase.CreateContext();
			context.Set<NotificationTemplate>().Add(new NotificationTemplate { TemplateKey = "case-updated", Language = language, Version = 1, IsActive = true, Subject = subject, Text = text, Html = html, CreatedAt = DateTime.UtcNow });
			await context.SaveChangesAsync();
		}

		private static CaseUpdatedData Model() => new() { UserName = "Sam", Case = new CaseInfo { Title = "Brake check", User = new UserInfo { DisplayName = "Sam Rivera" } } };

		private static async Task SendAsync(ServiceProvider provider, IEnumerable<TemplateRecipient> recipients, bool save = true)
		{
			using var scope = provider.CreateScope();
			var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
			await scope.ServiceProvider.GetRequiredService<INotificationSender>().CreateMobileNotificationFromTemplateAsync(repository, notificationType: 3, entityId: 42, Model(), "{\"id\":42}", recipients);
			if (save) await repository.SaveAsync();
		}

		private Task<List<Notification>> NotificationsAsync() => mDatabase.QueryAsync(db => db.Set<Notification>().AsNoTracking().OrderBy(n => n.Id).ToListAsync());

		private Task<List<NotificationToUser>> LinksAsync() => mDatabase.QueryAsync(db => db.Set<NotificationToUser>().AsNoTracking().OrderBy(n => n.UserId).ToListAsync());

		[Fact]
		public async Task SubjectBecomesTheTitle_TextTheBody_AndTheRestIsAnOrdinaryPush()
		{
			await AddTemplateAsync("Case {{ case.title }}", "{{ user_name }} changed it.", html: "<p>not used</p>");
			await using var provider = BuildHost();

			await SendAsync(provider, [new TemplateRecipient(10), new TemplateRecipient(20)]);

			var notification = (await NotificationsAsync()).ShouldHaveSingleItem();
			notification.Title.ShouldBe("Case Brake check");
			notification.Body.ShouldBe("Sam changed it.");
			notification.Payload.ShouldBe("{\"id\":42}");
			notification.TypeId.ShouldBe((short)3);
			notification.EntityId.ShouldBe(42);
			notification.TransportType.ShouldBe(NotificationTransportType.FCM);
			(await LinksAsync()).Select(l => l.UserId).ShouldBe([10, 20]);
			(await mDatabase.QueryAsync(db => db.Set<OutboxMessage>().CountAsync())).ShouldBe(0); // fan-out happens later, in the service
		}

		[Fact]
		public async Task EachLanguageGetsItsOwnNotification_WithItsOwnUsers()
		{
			await AddTemplateAsync("english", "body en");
			await AddTemplateAsync("ukrainian", "body uk", language: "uk");
			await using var provider = BuildHost();

			await SendAsync(provider, [new TemplateRecipient(1, "uk-UA"), new TemplateRecipient(2, "en"), new TemplateRecipient(3, "uk"), new TemplateRecipient(4)]);

			var notifications = await NotificationsAsync();
			notifications.Select(n => n.Title).OrderBy(t => t).ShouldBe(["english", "ukrainian"]);
			var links = await LinksAsync();
			var english = notifications.Single(n => n.Title == "english");
			var ukrainian = notifications.Single(n => n.Title == "ukrainian");
			links.Where(l => l.NotificationId == ukrainian.Id).Select(l => l.UserId).ShouldBe([1, 3]);
			links.Where(l => l.NotificationId == english.Id).Select(l => l.UserId).ShouldBe([2, 4]);
		}

		[Fact]
		public async Task LanguagesThatFallBackToTheSameTemplateShareOneNotification()
		{
			await AddTemplateAsync("english", "body");
			await using var provider = BuildHost();

			await SendAsync(provider, [new TemplateRecipient(1, "fr"), new TemplateRecipient(2, "de"), new TemplateRecipient(3, null)]);

			(await NotificationsAsync()).ShouldHaveSingleItem();
			(await LinksAsync()).Count.ShouldBe(3);
		}

		[Fact]
		public async Task AUserListedTwiceIsNotifiedOnce_InTheLanguageOfTheFirstEntry()
		{
			await AddTemplateAsync("english", "body");
			await AddTemplateAsync("ukrainian", "body", language: "uk");
			await using var provider = BuildHost();

			await SendAsync(provider, [new TemplateRecipient(1, "uk"), new TemplateRecipient(1, "en")]);

			var notification = (await NotificationsAsync()).ShouldHaveSingleItem();
			notification.Title.ShouldBe("ukrainian");
			(await LinksAsync()).ShouldHaveSingleItem().UserId.ShouldBe(1);
		}

		[Fact]
		public async Task ATemplateWithNoText_GivesAPushWithATitleOnly()
		{
			await AddTemplateAsync("only a title", null);
			await using var provider = BuildHost();

			await SendAsync(provider, [new TemplateRecipient(1)]);

			var notification = (await NotificationsAsync()).Single();
			notification.Title.ShouldBe("only a title");
			notification.Body.ShouldBeNull();
		}

		[Fact]
		public async Task AFailureInAnyLanguageWritesNothing()
		{
			await AddTemplateAsync("english", "body");
			await AddTemplateAsync("broken {{ nope }}", "body", language: "uk");
			await using var provider = BuildHost();

			await Should.ThrowAsync<TemplateRenderException>(() => SendAsync(provider, [new TemplateRecipient(1, "en"), new TemplateRecipient(2, "uk")]));

			(await NotificationsAsync()).ShouldBeEmpty();
			(await LinksAsync()).ShouldBeEmpty();
		}

		[Fact]
		public async Task ASecureTagInAPushTemplateIsRefused_BecauseTheRowIsKeptAsWritten()
		{
			await AddTemplateAsync("Code", "your code is <secure>123456</secure>");
			await using var provider = BuildHost();

			var ex = await Should.ThrowAsync<TemplateRenderException>(() => SendAsync(provider, [new TemplateRecipient(1)]));

			ex.Part.ShouldBe("Text");
			ex.Message.ShouldContain("secure");
			ex.Message.ShouldNotContain("123456");
			(await NotificationsAsync()).ShouldBeEmpty();
		}

		[Fact]
		public async Task AMissingTemplate_NoRecipients_AndNoRegistration_AreEachRefusedClearly()
		{
			await using var provider = BuildHost();
			await Should.ThrowAsync<TemplateNotFoundException>(() => SendAsync(provider, [new TemplateRecipient(1)]));

			await AddTemplateAsync("english", "body");
			await Should.ThrowAsync<ArgumentException>(() => SendAsync(provider, []));

			await using var bare = BuildHost(templatesOn: false);
			(await Should.ThrowAsync<InvalidOperationException>(() => SendAsync(bare, [new TemplateRecipient(1)]))).Message.ShouldContain("AddNotificationTemplates");
			(await NotificationsAsync()).ShouldBeEmpty();
		}

		[Fact]
		public async Task ANotSavedCallLeavesNothingBehind()
		{
			await AddTemplateAsync("english", "body");
			await using var provider = BuildHost();

			await SendAsync(provider, [new TemplateRecipient(1)], save: false);

			(await NotificationsAsync()).ShouldBeEmpty();
		}
	}
}
