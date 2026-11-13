using exs.Database.Commons.Interfaces;
using exs.notifications_contracts;
using exs.notifications_model.Templates;
using exs.notifications_service.Templates;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.notifications_service.Tests.Templates
{
	public sealed class UserInfo
	{
		public required string DisplayName { get; init; }

		public string? Phone { get; init; }
	}

	public sealed class CaseInfo
	{
		public required UserInfo User { get; init; }

		public required string Title { get; init; }
	}

	public sealed class LineItem
	{
		public required string Name { get; init; }
	}

	public sealed class CaseUpdatedData : ITemplateModel
	{
		public static string TemplateKey => "case-updated";

		public required string UserName { get; init; }

		public required CaseInfo Case { get; init; }

		public string? Nickname { get; init; }

		public List<LineItem> Items { get; init; } = [];

		public string Code { get; init; } = "123456";
	}

	/// <summary>A second model, so the registry has more than one to tell apart.</summary>
	public sealed class OtherData : ITemplateModel
	{
		public static string TemplateKey => "other";

		public required string Value { get; init; }
	}

	public sealed class TemplateProcessorTests : IAsyncDisposable
	{
		private readonly HostTestDatabase mDatabase = HostTestDatabase.Create();
		private readonly TemplateProcessor mProcessor;

		public TemplateProcessorTests()
		{
			mProcessor = createProcessor();
		}

		public async ValueTask DisposeAsync() => await mDatabase.DisposeAsync();

		private static TemplateProcessor createProcessor(string defaultLanguage = "en") =>
			new(new TemplateRegistry([typeof(CaseUpdatedData), typeof(OtherData)]), new StaticOptions(new NotificationTemplateOptions { DefaultLanguage = defaultLanguage }));

		private sealed class StaticOptions(NotificationTemplateOptions value) : IOptionsMonitor<NotificationTemplateOptions>
		{
			public NotificationTemplateOptions CurrentValue => value;

			public NotificationTemplateOptions Get(string? name) => value;

			public IDisposable? OnChange(Action<NotificationTemplateOptions, string?> listener) => null;
		}

		private async Task AddAsync(string key, string language, string subject, string? text = null, string? html = null, int version = 1, bool active = true)
		{
			await using var context = mDatabase.CreateContext();
			context.Set<NotificationTemplate>().Add(new NotificationTemplate { TemplateKey = key, Language = language, Version = version, IsActive = active, Subject = subject, Text = text, Html = html, CreatedAt = DateTime.UtcNow });
			await context.SaveChangesAsync();
		}

		private static CaseUpdatedData model() => new() { UserName = "Sam", Case = new CaseInfo { Title = "Brake check", User = new UserInfo { DisplayName = "Sam Rivera", Phone = "+1 555 0100" } } };

		private async Task<RenderedTemplate> RenderAsync(CaseUpdatedData data, string? language = null)
		{
			using var repository = mDatabase.CreateRepository();
			return await mProcessor.RenderAsync(repository, data, language);
		}

		[Fact]
		public async Task ReadsNestedValuesInSnakeCase_InAllThreeParts()
		{
			await AddAsync("case-updated", "en", "Case {{ case.title }}", "Hello {{ user_name }}, {{ case.user.display_name }} changed it.", "<p>Hello {{ user_name }}</p>");

			var result = await RenderAsync(model());

			result.Subject.ShouldBe("Case Brake check");
			result.Text.ShouldBe("Hello Sam, Sam Rivera changed it.");
			result.Html.ShouldBe("<p>Hello Sam</p>");
			result.TemplateKey.ShouldBe("case-updated");
			result.Language.ShouldBe("en");
			result.Version.ShouldBe(1);
		}

		[Fact]
		public async Task HtmlEscapesValues_ButSubjectAndTextDoNot()
		{
			await AddAsync("case-updated", "en", "{{ case.title }}", "{{ case.title }}", "<p>{{ case.title }}</p>");
			var data = new CaseUpdatedData { UserName = "x", Case = new CaseInfo { Title = "A <b> & B", User = new UserInfo { DisplayName = "d" } } };

			var result = await RenderAsync(data);

			result.Subject.ShouldBe("A <b> & B");
			result.Text.ShouldBe("A <b> & B");
			result.Html.ShouldBe("<p>A &lt;b&gt; &amp; B</p>");
		}

		[Fact]
		public async Task ALoopOverAListOfObjectsWorks()
		{
			await AddAsync("case-updated", "en", "S", "{% for item in items %}[{{ item.name }}]{% endfor %}");
			var data = model();
			var withItems = new CaseUpdatedData { UserName = data.UserName, Case = data.Case, Items = [new LineItem { Name = "a" }, new LineItem { Name = "b" }] };

			(await RenderAsync(withItems)).Text.ShouldBe("[a][b]");
		}

		[Fact]
		public async Task OnlyTheActiveVersionIsUsed()
		{
			await AddAsync("case-updated", "en", "old", version: 1, active: false);
			await AddAsync("case-updated", "en", "new", version: 2, active: true);

			var result = await RenderAsync(model());

			result.Subject.ShouldBe("new");
			result.Version.ShouldBe(2);
		}

		[Fact]
		public async Task ANullableValueThatIsMissingIsFine_AndTheTemplateCanTestForIt()
		{
			await AddAsync("case-updated", "en", "S", "{% if nickname %}Hi {{ nickname }}{% else %}Hi there{% endif %}");

			(await RenderAsync(model())).Text.ShouldBe("Hi there");
		}

		[Fact]
		public async Task AMissingKeyThrows_NamingTheKeyAndTheLanguagesTried()
		{
			var ex = await Should.ThrowAsync<TemplateNotFoundException>(() => RenderAsync(model(), "uk-UA"));

			ex.TemplateKey.ShouldBe("case-updated");
			ex.LanguagesTried.ShouldBe(["uk-ua", "uk", "en"]);
		}

		[Fact]
		public async Task AnInactiveTemplateCountsAsMissing()
		{
			await AddAsync("case-updated", "en", "S", active: false);

			await Should.ThrowAsync<TemplateNotFoundException>(() => RenderAsync(model()));
		}

		[Fact]
		public async Task TheWantedLanguageWins_ThenItsBaseLanguage_ThenTheDefault()
		{
			await AddAsync("case-updated", "en", "english");
			await AddAsync("case-updated", "uk", "ukrainian");
			await AddAsync("case-updated", "pt-br", "brazilian");

			(await RenderAsync(model(), "pt_BR")).Subject.ShouldBe("brazilian");
			(await RenderAsync(model(), "uk-UA")).Subject.ShouldBe("ukrainian");
			(await RenderAsync(model(), "de")).Subject.ShouldBe("english");
			(await RenderAsync(model(), null)).Subject.ShouldBe("english");
			(await RenderAsync(model(), "uk-UA")).Language.ShouldBe("uk");
		}

		[Fact]
		public async Task ANullInANonNullableProperty_IsRefusedBeforeAnyRendering_NamingThePathOnly()
		{
			await AddAsync("case-updated", "en", "S");
			var broken = new CaseUpdatedData { UserName = "Sam", Case = new CaseInfo { Title = "secret-title", User = null! } };

			var ex = await Should.ThrowAsync<TemplateModelException>(() => RenderAsync(broken));

			ex.MissingPaths.ShouldBe(["Case.User"]);
			ex.Message.ShouldNotContain("secret-title");
		}

		[Fact]
		public async Task AMissingValueInsideAListItemIsFoundToo()
		{
			await AddAsync("case-updated", "en", "S");
			var data = model();
			var broken = new CaseUpdatedData { UserName = "Sam", Case = data.Case, Items = [new LineItem { Name = null! }] };

			var ex = await Should.ThrowAsync<TemplateModelException>(() => RenderAsync(broken));

			ex.MissingPaths.ShouldBe(["Items[0].Name"]);
		}

		[Fact]
		public async Task AVariableThatDoesNotExistIsAnError_NotAnEmptyString()
		{
			await AddAsync("case-updated", "en", "S", "Hello {{ user_nmae }}");

			var ex = await Should.ThrowAsync<TemplateRenderException>(() => RenderAsync(model()));

			ex.Part.ShouldBe("Text");
			ex.Message.ShouldContain("user_nmae");
			ex.TemplateKey.ShouldBe("case-updated");
		}

		[Fact]
		public async Task AMistypedNestedName_IsAnErrorNamingWhatTheModelHas_EvenWhenNothingWouldBeRendered()
		{
			await AddAsync("case-updated", "en", "S", "{% if false %}{{ case.user.display_nmae }}{% endif %}");

			var ex = await Should.ThrowAsync<TemplateRenderException>(() => RenderAsync(model()));

			ex.Part.ShouldBe("Text");
			ex.Message.ShouldContain("case.user.display_nmae");
			ex.Message.ShouldContain("display_name");
		}

		[Fact]
		public async Task NamesThatTheTemplateMakesItselfAreNotMistaken_ForMissingValues()
		{
			await AddAsync("case-updated", "en", "S", "{% assign greeting = user_name %}{% for item in items %}{{ item.anything }}{% endfor %}{{ greeting }}");

			(await RenderAsync(model())).Text.ShouldBe("Sam");
		}

		[Fact]
		public async Task NonLatinTextIsKeptAsIsInHtml_AndOnlyTheHtmlCharactersAreEscaped()
		{
			await AddAsync("case-updated", "en", "{{ user_name }}", html: "<p>{{ user_name }} & {{ case.title }}</p>");
			var data = new CaseUpdatedData { UserName = "Олена", Case = new CaseInfo { Title = "<Ремонт>", User = new UserInfo { DisplayName = "d" } } };

			var result = await RenderAsync(data);

			result.Subject.ShouldBe("Олена");
			result.Html.ShouldBe("<p>Олена & &lt;Ремонт&gt;</p>");
		}

		[Fact]
		public async Task ATemplateThatDoesNotParseIsAnErrorNamingThePart()
		{
			await AddAsync("case-updated", "en", "S", html: "<p>{% if %}</p>");

			var ex = await Should.ThrowAsync<TemplateRenderException>(() => RenderAsync(model()));

			ex.Part.ShouldBe("Html");
		}

		[Fact]
		public async Task AnUnknownFilterIsAnError()
		{
			await AddAsync("case-updated", "en", "S", "{{ user_name | no_such_filter }}");

			await Should.ThrowAsync<TemplateRenderException>(() => RenderAsync(model()));
		}

		[Fact]
		public async Task TheSecureTagsOfTheTemplateSurvive_SoTheSenderCanMaskThem()
		{
			await AddAsync("case-updated", "en", "S", "Code <secure>{{ code }}</secure> for {{ user_name }}", "<a href=\"<secure>https://x/{{ code }}</secure>\">go</a>");

			var result = await RenderAsync(model());

			result.Text.ShouldBe("Code <secure>123456</secure> for Sam");
			SecureMarkup.Mask(result.Text).ShouldBe("Code XXXX for Sam");
			SecureMarkup.Mask(result.Html).ShouldBe("<a href=\"XXXX\">go</a>");
		}

		[Fact]
		public async Task AValueCannotFormATag_InAnyPart()
		{
			await AddAsync("case-updated", "en", "Re {{ user_name }}", "Hello <secure>{{ user_name }}</secure> and {{ case.title }}", "<p><secure>{{ user_name }}</secure> {{ case.title }}</p>");
			var data = new CaseUpdatedData { UserName = "Bob</secure> leaked <secure>", Case = new CaseInfo { Title = "<secure>", User = new UserInfo { DisplayName = "d" } } };

			var result = await RenderAsync(data);

			SecureMarkup.Validate(result.Subject).ShouldBeNull();
			SecureMarkup.HasTags(result.Subject).ShouldBeFalse();
			// What the author put between the tags stays between them, whatever the value was.
			SecureMarkup.Mask(result.Text).ShouldBe("Hello XXXX and ＜secure>");
			SecureMarkup.Mask(result.Html).ShouldBe("<p>XXXX ＜secure&gt;</p>");
		}

		[Fact]
		public async Task ATemplateWithUnbalancedTagsIsAnError()
		{
			await AddAsync("case-updated", "en", "S", "Code <secure>{{ code }}");

			var ex = await Should.ThrowAsync<TemplateRenderException>(() => RenderAsync(model()));

			ex.Message.ShouldContain("secure");
			ex.Message.ShouldNotContain("123456");
		}

		[Fact]
		public async Task APartWithNoSource_IsNull()
		{
			await AddAsync("case-updated", "en", "S");

			var result = await RenderAsync(model());

			result.Text.ShouldBeNull();
			result.Html.ShouldBeNull();
		}

		[Fact]
		public async Task AModelThatWasNotRegisteredIsRefused()
		{
			using var repository = mDatabase.CreateRepository();
			var processor = new TemplateProcessor(new TemplateRegistry([typeof(OtherData)]), new StaticOptions(new NotificationTemplateOptions()));

			await Should.ThrowAsync<InvalidOperationException>(() => processor.RenderAsync(repository, model()));
		}

		[Fact]
		public void TwoModelsWithOneKeyAreRefusedAtStartup()
		{
			Should.Throw<InvalidOperationException>(() => new TemplateRegistry([typeof(OtherData), typeof(OtherData)])).Message.ShouldContain("other");
		}

		[Fact]
		public void TheRegistryFindsModelsByScanningAnAssembly()
		{
			var registry = new TemplateRegistry([typeof(TemplateProcessorTests).Assembly]);

			registry.TryGetModelType("case-updated", out var type).ShouldBeTrue();
			type.ShouldBe(typeof(CaseUpdatedData));
		}

		[Fact]
		public async Task AddNotificationTemplates_RegistersTheProcessor_AndBindsTheDefaultLanguage()
		{
			var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["NotificationService:Templates:DefaultLanguage"] = "uk" }).Build();
			var services = new ServiceCollection();
			mDatabase.Register(services);
			services.AddNotificationTemplates([typeof(CaseUpdatedData).Assembly], configuration);
			await using var provider = services.BuildServiceProvider();
			await AddAsync("case-updated", "uk", "ukrainian");

			using var scope = provider.CreateScope();
			var result = await provider.GetRequiredService<TemplateProcessor>().RenderAsync(scope.ServiceProvider.GetRequiredService<IRepository>(), model(), "fr");

			result.Subject.ShouldBe("ukrainian");
		}

		[Fact]
		public async Task TheDatabaseRefusesASecondActiveVersionOfOneKeyAndLanguage()
		{
			await AddAsync("case-updated", "en", "one", version: 1, active: true);

			await Should.ThrowAsync<DbUpdateException>(() => AddAsync("case-updated", "en", "two", version: 2, active: true));
		}

		[Fact]
		public async Task TheDatabaseRefusesTheSameVersionTwice()
		{
			await AddAsync("case-updated", "en", "one", version: 1, active: false);

			await Should.ThrowAsync<DbUpdateException>(() => AddAsync("case-updated", "en", "one again", version: 1, active: false));
		}
	}
}
