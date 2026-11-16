using System.Text.Json.Nodes;
using exs.notifications_contracts;
using exs.notifications_model.Templates;
using exs.notifications_service.Templates;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.Extensions.Options;
using Shouldly;

namespace exs.notifications_service.Tests.Templates
{
	public enum Priority
	{
		Normal,
		Urgent,
	}

	/// <summary>One of each kind of value a template can read, for the description of a model.</summary>
	public sealed class KindsData : ITemplateModel
	{
		public static string TemplateKey => "kinds";

		public required string Name { get; init; }

		public int Count { get; init; }

		public decimal? Amount { get; init; }

		public bool Flag { get; init; }

		public DateTime When { get; init; }

		public DateTimeOffset? Until { get; init; }

		public Priority Priority { get; init; }

		public List<string> Tags { get; init; } = [];

		public Dictionary<string, string> Extra { get; init; } = [];
	}

	public sealed class TemplateAuthoringTests : IAsyncDisposable
	{
		private readonly HostTestDatabase mDatabase = HostTestDatabase.Create();
		private readonly TemplateAuthoring mAuthoring;
		private readonly TemplateProcessor mProcessor;

		public TemplateAuthoringTests()
		{
			var registry = new TemplateRegistry([typeof(CaseUpdatedData), typeof(OtherData), typeof(KindsData)]);
			mProcessor = new TemplateProcessor(registry, new StaticOptions(new NotificationTemplateOptions { DefaultLanguage = "en" }));
			mAuthoring = new TemplateAuthoring(registry, mProcessor);
		}

		public async ValueTask DisposeAsync() => await mDatabase.DisposeAsync();

		private sealed class StaticOptions(NotificationTemplateOptions value) : IOptionsMonitor<NotificationTemplateOptions>
		{
			public NotificationTemplateOptions CurrentValue => value;

			public NotificationTemplateOptions Get(string? name) => value;

			public IDisposable? OnChange(Action<NotificationTemplateOptions, string?> listener) => null;
		}

		private TemplateModelDescription Describe<T>() where T : ITemplateModel => mAuthoring.Describe(T.TemplateKey, typeof(T));

		// ---- the description of a model ----

		[Fact]
		public void Describe_ListsEveryLeafPathAsDeclared_WithTypeAndOptional_ForNestedObjectsOptionalValuesAndLists()
		{
			var description = Describe<CaseUpdatedData>();

			description.Key.ShouldBe("case-updated");
			description.Variables.Select(v => (v.Path, v.Type, v.Optional)).ToList().ShouldBe(
			[
				("UserName", "string", false),
				("Case.User.DisplayName", "string", false),
				("Case.User.Phone","string", true),
				("Case.Title", "string", false),
				("Nickname", "string", true),
				("Items", "list", false),
				("Items[].Name", "string", false),
				("Code", "string", false),
			]);
		}

		[Fact]
		public void Describe_TellsNumbersFlagsDatesEnumsAndLists_AndLeavesDictionariesOut()
		{
			var variables = Describe<KindsData>().Variables.ToDictionary(v => v.Path);

			variables.Keys.ShouldBe(["Name", "Count", "Amount", "Flag", "When", "Until", "Priority", "Tags"]);
			variables["Count"].Type.ShouldBe("number");
			variables["Count"].Optional.ShouldBeFalse();
			variables["Amount"].Type.ShouldBe("number");
			variables["Amount"].Optional.ShouldBeTrue();
			variables["Flag"].Type.ShouldBe("boolean");
			variables["When"].Type.ShouldBe("datetime");
			variables["Until"].Type.ShouldBe("datetime");
			variables["Until"].Optional.ShouldBeTrue();
			variables["Priority"].Type.ShouldBe("string");
			variables["Tags"].Type.ShouldBe("list");
		}

		[Fact]
		public void DescribeModels_HasOneEntryPerRegisteredModel_SortedByKey()
		{
			mAuthoring.DescribeModels().Select(m => m.Key).ToList().ShouldBe(["case-updated", "kinds", "other"]);
		}

		// ---- the sample ----

		[Fact]
		public void CreateSample_FillsEveryKindOfValue_WithPlausibleOnes()
		{
			var sample = TemplateAuthoring.CreateSample(typeof(CaseUpdatedData));

			sample["UserName"]!.GetValue<string>().ShouldBe("Sample UserName");
			sample["Case"]!["User"]!["DisplayName"]!.GetValue<string>().ShouldBe("Sample DisplayName");
			sample["Case"]!["User"]!["Phone"]!.GetValue<string>().ShouldBe("Sample Phone");
			sample["Nickname"]!.GetValue<string>().ShouldBe("Sample Nickname");
			var items = sample["Items"]!.AsArray();
			items.Count.ShouldBe(2);
			items[0]!["Name"]!.GetValue<string>().ShouldBe("Sample Name");
			sample.ContainsKey("userName").ShouldBeFalse();

			var kinds = TemplateAuthoring.CreateSample(typeof(KindsData));
			kinds["Count"]!.GetValue<int>().ShouldBe(1);
			kinds["Amount"]!.GetValue<int>().ShouldBe(1);
			kinds["Flag"]!.GetValue<bool>().ShouldBeTrue();
			kinds["Priority"]!.GetValue<string>().ShouldBe("Normal");
			kinds["Tags"]!.AsArray().Count.ShouldBe(2);
			kinds["Extra"]!.AsObject().Count.ShouldBe(0);
			var when = DateTime.Parse(kinds["When"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind);
			(DateTime.UtcNow - when).Duration().ShouldBeLessThan(TimeSpan.FromMinutes(5));
		}

		[Fact]
		public void CreateModel_BuildsTheModelFromTheSample_AndLaysAnOverrideOverIt()
		{
			var model = (CaseUpdatedData)TemplateAuthoring.CreateModel("case-updated", typeof(CaseUpdatedData));
			model.Case.User.DisplayName.ShouldBe("Sample DisplayName");
			model.Items.Count.ShouldBe(2);

			var overridden = (CaseUpdatedData)TemplateAuthoring.CreateModel("case-updated", typeof(CaseUpdatedData), JsonNode.Parse("""{"UserName":"Sam","Case":{"User":{"DisplayName":"Sam Rivera"}},"Items":[{"Name":"only"}]}""")!.AsObject());
			overridden.UserName.ShouldBe("Sam");
			overridden.Case.User.DisplayName.ShouldBe("Sam Rivera");
			overridden.Case.Title.ShouldBe("Sample Title");
			overridden.Items.Select(i => i.Name).ToList().ShouldBe(["only"]);
		}

		[Fact]
		public void CreateModel_WithAValueOfTheWrongType_FailsNamingThePath_NotTheValue()
		{
			var wrong = JsonNode.Parse("""{"Case":{"Title":{"a":1}}}""")!.AsObject();

			var ex = Should.Throw<TemplateModelJsonException>(() => TemplateAuthoring.CreateModel("case-updated", typeof(CaseUpdatedData), wrong));

			ex.Message.ShouldContain("case-updated");
			ex.Message.ShouldContain("Title");
		}

		// ---- drafts ----

		private async Task<RenderedTemplate> DraftAsync(string subject, string? text, string? html = null, string? language = null, CaseUpdatedData? data = null) =>
			await mProcessor.RenderDraftAsync("case-updated", subject, text, html, data ?? (CaseUpdatedData)TemplateAuthoring.CreateModel("case-updated", typeof(CaseUpdatedData)), language);

		[Fact]
		public async Task RenderDraft_FillsTheThreePartsFromTheStringsGiven_WithoutADatabase()
		{
			var result = await DraftAsync("Case {{ Case.Title }}", "Hello {{ UserName }}, {{ Case.User.DisplayName }}", "<p>{{ UserName }}</p>");

			result.Subject.ShouldBe("Case Sample Title");
			result.Text.ShouldBe("Hello Sample UserName, Sample DisplayName");
			result.Html.ShouldBe("<p>Sample UserName</p>");
			result.TemplateKey.ShouldBe("case-updated");
			result.Language.ShouldBe("en");
			result.Version.ShouldBe(0);
		}

		[Fact]
		public async Task RenderDraft_HtmlEscapesValuesInTheHtmlPart_ButNotInSubjectOrText()
		{
			var data = new CaseUpdatedData { UserName = "x", Case = new CaseInfo { Title = "A <b> & B", User = new UserInfo { DisplayName = "d" } } };

			var result = await DraftAsync("{{ Case.Title }}", "{{ Case.Title }}", "<p>{{ Case.Title }}</p>", data: data);

			result.Subject.ShouldBe("A <b> & B");
			result.Text.ShouldBe("A <b> & B");
			result.Html.ShouldBe("<p>A &lt;b&gt; &amp; B</p>");
		}

		[Fact]
		public async Task RenderDraft_UsesTheCultureOfTheLanguage()
		{
			var text = "{{ '2024-03-05' | date: '%B' }}";

			(await DraftAsync("S", text, language: "en")).Text.ShouldBe("March");
			(await DraftAsync("S", text, language: "uk")).Text.ShouldNotBe("March");
		}

		[Fact]
		public async Task Preview_RefusesAWrongCaseName_ExactlyAsARealSendWould_ListingTheNamesTheModelHas()
		{
			var ex = await Should.ThrowAsync<TemplateRenderException>(() => mAuthoring.PreviewAsync("case-updated", "en", "S", "{{ userName }}", null));

			ex.Part.ShouldBe("Text");
			ex.Message.ShouldContain("userName");
			ex.Message.ShouldContain("UserName, Case, Nickname, Items, Code");

			await Should.ThrowAsync<TemplateRenderException>(() => mAuthoring.PreviewAsync("case-updated", "en", "S", "{{ user_name }}", null));
		}

		[Fact]
		public async Task RenderDraft_WithAnUnknownValue_ThrowsATemplateRenderException_NamingThePart()
		{
			var ex = await Should.ThrowAsync<TemplateRenderException>(() => DraftAsync("S", "Hi {{ Case.User.DisplayNmae }}"));

			ex.Part.ShouldBe("Text");
			ex.Problem.ShouldContain("DisplayNmae");

			var first = await Should.ThrowAsync<TemplateRenderException>(() => DraftAsync("S", "x", "<p>{{ nope }}</p>"));
			first.Part.ShouldBe("Html");
		}

		[Fact]
		public async Task RenderDraft_WithATemplateThatDoesNotParse_ThrowsATemplateRenderException()
		{
			var ex = await Should.ThrowAsync<TemplateRenderException>(() => DraftAsync("{% if %}", "x"));

			ex.Part.ShouldBe("Subject");
		}

		[Fact]
		public async Task RenderDraft_WithAnUnbalancedSecureTag_ThrowsATemplateRenderException()
		{
			var ex = await Should.ThrowAsync<TemplateRenderException>(() => DraftAsync("S", "code <secure>{{ Code }}"));

			ex.Part.ShouldBe("Text");
			ex.Problem.ShouldContain("secure");
		}

		[Fact]
		public async Task RenderDraft_KeepsTheSecureTags_ButAValueCannotFormOne()
		{
			var data = new CaseUpdatedData { UserName = "</secure>", Case = new CaseInfo { Title = "t", User = new UserInfo { DisplayName = "d" } } };

			var result = await DraftAsync("S", "a <secure>{{ Code }}</secure> {{ UserName }}", data: data);

			result.Text!.ShouldStartWith("a <secure>123456</secure> ");
			SecureMarkup.Validate(result.Text).ShouldBeNull();
		}

		[Fact]
		public async Task RenderDraft_ForAKeyWithoutAModel_Throws()
		{
			await Should.ThrowAsync<InvalidOperationException>(() => mProcessor.RenderDraftAsync("nothing", "S", "x", null, new object()));
		}

		[Fact]
		public async Task RenderDraft_ForANonNullableValueThatIsNull_ThrowsATemplateModelException()
		{
			var data = new CaseUpdatedData { UserName = "x", Case = null!, };

			await Should.ThrowAsync<TemplateModelException>(() => DraftAsync("S", "x", data: data));
		}

		// ---- preview ----

		[Fact]
		public async Task Preview_ReturnsTheTextAsTheRecipientSeesIt_WithSecureTagsRemovedAndContentKept()
		{
			var result = await mAuthoring.PreviewAsync("case-updated", "en", "Code <secure>{{ Code }}</secure>", "Your code: <secure>{{ Code }}</secure>", "<p><a href=\"<secure>https://x/{{ Code }}</secure>\">go</a></p>");

			result.Subject.ShouldBe("Code Sample Code");
			result.Text.ShouldBe("Your code: Sample Code");
			result.Html.ShouldBe("<p><a href=\"https://x/Sample Code\">go</a></p>");
		}

		[Fact]
		public async Task Preview_WithoutOverride_UsesTheSample_AndWithOneUsesIt()
		{
			(await mAuthoring.PreviewAsync("case-updated", null, "{{ UserName }}", null, "<b>{{ UserName }}</b>")).Subject.ShouldBe("Sample UserName");

			var overridden = await mAuthoring.PreviewAsync("case-updated", null, "{{ UserName }}", "{{ Nickname }}", null, JsonNode.Parse("""{"UserName":"Sam","Nickname":"Sammy"}""")!.AsObject());
			overridden.Subject.ShouldBe("Sam");
			overridden.Text.ShouldBe("Sammy");
			overridden.Html.ShouldBeNull();
		}

		[Fact]
		public async Task Preview_RejectsAnUnbalancedSecureTag()
		{
			await Should.ThrowAsync<TemplateRenderException>(() => mAuthoring.PreviewAsync("case-updated", "en", "S", "</secure>", null));
		}

		[Fact]
		public async Task Preview_WithAnOverrideThatDoesNotFitTheModel_ThrowsTemplateModelJsonException()
		{
			await Should.ThrowAsync<TemplateModelJsonException>(() => mAuthoring.PreviewAsync("case-updated", "en", "S", "x", null, JsonNode.Parse("""{"Items":"not a list"}""")!.AsObject()));
		}

		// ---- input checks ----

		[Theory]
		[InlineData("en", "S", "t", null, null)]
		[InlineData("pt-BR", "S", null, "<p>h</p>", null)]
		[InlineData("", "S", "t", null, "The language is required.")]
		[InlineData(null, "S", "t", null, "The language is required.")]
		[InlineData("this-is-far-too-long-a-tag", "S", "t", null, "The language must be at most 16 characters.")]
		[InlineData("en_US", "S", "t", null, "The language must be a language tag: letters, optionally separated by dashes (en, uk, pt-br).")]
		[InlineData("en-", "S", "t", null, "The language must be a language tag: letters, optionally separated by dashes (en, uk, pt-br).")]
		[InlineData("en", " ", "t", null, "The subject is required.")]
		[InlineData("en", "S", " ", null, "The template needs a text or an HTML part.")]
		public void CheckDraft_FindsWhatIsWrongBeforeAnyRendering(string? language, string? subject, string? text, string? html, string? expected)
		{
			TemplateAuthoring.CheckDraft(language, subject, text, html).ShouldBe(expected);
		}

		[Fact]
		public void CheckDraft_AcceptsNoLanguage_WhenItIsNotRequired()
		{
			TemplateAuthoring.CheckDraft(null, "S", "t", null, languageRequired: false).ShouldBeNull();
		}

		// ---- versions ----

		[Fact]
		public async Task CreateVersion_AddsInactiveVersionsNumberedPerLanguage_AndStoresTheTagLowerCase()
		{
			using var repository = mDatabase.CreateRepository();

			var first = await mAuthoring.CreateVersionAsync(repository, "case-updated", "EN", "S1", "t", null, "sam");
			var second = await mAuthoring.CreateVersionAsync(repository, "case-updated", "en", "S2", null, "<p>h</p>", "sam");
			var other = await mAuthoring.CreateVersionAsync(repository, "case-updated", "uk", "S3", "t", null, null);

			first.Version.ShouldBe(1);
			first.Language.ShouldBe("en");
			first.IsActive.ShouldBeFalse();
			first.CreatedBy.ShouldBe("sam");
			(DateTime.UtcNow - first.CreatedAt).Duration().ShouldBeLessThan(TimeSpan.FromMinutes(1));
			second.Version.ShouldBe(2);
			second.IsActive.ShouldBeFalse();
			second.Text.ShouldBeNull();
			other.Version.ShouldBe(1);
			other.Id.ShouldNotBe(first.Id);
		}

		[Fact]
		public async Task CreateVersion_WithADraftThatDoesNotRender_ThrowsAndWritesNothing()
		{
			using var repository = mDatabase.CreateRepository();

			await Should.ThrowAsync<TemplateRenderException>(() => mAuthoring.CreateVersionAsync(repository, "case-updated", "en", "S", "{{ nope }}", null, "sam"));

			(await mDatabase.QueryAsync(db => Task.FromResult(db.Set<NotificationTemplate>().Count()))).ShouldBe(0);
		}

		[Fact]
		public async Task CreateVersion_LeavesTheActiveVersionAlone_SoTheProcessorStillUsesIt()
		{
			using var repository = mDatabase.CreateRepository();
			var live = await mAuthoring.CreateVersionAsync(repository, "case-updated", "en", "live", "t", null, null);
			await mAuthoring.ActivateAsync(repository, live.Id);

			await mAuthoring.CreateVersionAsync(repository, "case-updated", "en", "draft", "t", null, null);

			using var reader = mDatabase.CreateRepository();
			var rendered = await mProcessor.RenderAsync(reader, (CaseUpdatedData)TemplateAuthoring.CreateModel("case-updated", typeof(CaseUpdatedData)));
			rendered.Subject.ShouldBe("live");
			rendered.Version.ShouldBe(1);
		}

		[Fact]
		public async Task ListVersions_GroupsByLanguage_NewestFirst_AndCanNarrowToOne()
		{
			using var repository = mDatabase.CreateRepository();
			await mAuthoring.CreateVersionAsync(repository, "case-updated", "uk", "u1", "t", null, null);
			await mAuthoring.CreateVersionAsync(repository, "case-updated", "en", "e1", "t", null, null);
			await mAuthoring.CreateVersionAsync(repository, "case-updated", "en", "e2", "t", null, null);
			await mAuthoring.CreateVersionAsync(repository, "other", "en", "o1", "t", null, null);

			var all = await mAuthoring.ListVersionsAsync(repository, "case-updated");
			all.Select(v => $"{v.Language}{v.Version}").ToList().ShouldBe(["en2", "en1", "uk1"]);

			var english = await mAuthoring.ListVersionsAsync(repository, "case-updated", "EN");
			english.Select(v => v.Subject).ToList().ShouldBe(["e2", "e1"]);
		}

		[Fact]
		public async Task Activate_SwitchesTheLiveVersionOfThatKeyAndLanguageOnly_AndIsRepeatable()
		{
			using var repository = mDatabase.CreateRepository();
			var v1 = await mAuthoring.CreateVersionAsync(repository, "case-updated", "en", "v1", "t", null, null);
			var v2 = await mAuthoring.CreateVersionAsync(repository, "case-updated", "en", "v2", "t", null, null);
			var uk = await mAuthoring.CreateVersionAsync(repository, "case-updated", "uk", "uk1", "t", null, null);

			(await mAuthoring.ActivateAsync(repository, v1.Id)).ShouldBeTrue();
			(await mAuthoring.ActivateAsync(repository, uk.Id)).ShouldBeTrue();
			(await activeAsync()).ShouldBe(["en:1", "uk:1"]);

			(await mAuthoring.ActivateAsync(repository, v2.Id)).ShouldBeTrue();
			(await activeAsync()).ShouldBe(["en:2", "uk:1"]);

			// Already active: nothing changes. And back to the first one again.
			(await mAuthoring.ActivateAsync(repository, v2.Id)).ShouldBeTrue();
			(await activeAsync()).ShouldBe(["en:2", "uk:1"]);
			(await mAuthoring.ActivateAsync(repository, v1.Id)).ShouldBeTrue();
			(await activeAsync()).ShouldBe(["en:1", "uk:1"]);
		}

		[Fact]
		public async Task Activate_OfAnUnknownId_ReturnsFalse()
		{
			using var repository = mDatabase.CreateRepository();

			(await mAuthoring.ActivateAsync(repository, 9999)).ShouldBeFalse();
		}

		private async Task<List<string>> activeAsync() =>
			await mDatabase.QueryAsync(db => Task.FromResult(db.Set<NotificationTemplate>().Where(t => t.IsActive).OrderBy(t => t.Language).Select(t => t.Language + ":" + t.Version).ToList()));

		[Fact]
		public async Task ListKeys_IsTheUnionOfKeysWithRowsAndKeysWithAModel_Sorted()
		{
			using var repository = mDatabase.CreateRepository();
			var en1 = await mAuthoring.CreateVersionAsync(repository, "case-updated", "en", "e1", "t", null, null);
			await mAuthoring.CreateVersionAsync(repository, "case-updated", "en", "e2", "t", null, null);
			await mAuthoring.CreateVersionAsync(repository, "case-updated", "uk", "u1", "t", null, null);
			await mAuthoring.ActivateAsync(repository, en1.Id);
			// A row whose model has since been removed from the code.
			await mDatabase.QueryAsync(async db =>
			{
				db.Set<NotificationTemplate>().Add(new NotificationTemplate { TemplateKey = "legacy", Language = "en", Version = 1, IsActive = true, Subject = "s", CreatedAt = DateTime.UtcNow });
				await db.SaveChangesAsync();
				return 0;
			});

			var keys = await mAuthoring.ListKeysAsync(repository);

			keys.Select(k => (k.Key, k.HasModel)).ToList().ShouldBe([("case-updated", true), ("kinds", true), ("legacy", false), ("other", true)]);
			var caseUpdated = keys.First(k => k.Key == "case-updated");
			caseUpdated.Languages.ShouldBe([new TemplateLanguageSummary("en", 1, 2), new TemplateLanguageSummary("uk", null, 1)]);
			keys.First(k => k.Key == "kinds").Languages.ShouldBeEmpty();
			keys.First(k => k.Key == "legacy").Languages.ShouldBe([new TemplateLanguageSummary("en", 1, 1)]);
		}
	}
}
