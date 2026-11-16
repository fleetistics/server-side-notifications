using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using exs.Database.Commons.Interfaces;
using exs.notifications_contracts;
using exs.notifications_model.Templates;
using Fluid;
using Microsoft.EntityFrameworkCore;

namespace exs.notifications_service.Templates
{
	/// <summary>One value a template may read: its path as the template writes it, its type and whether the model may leave it null.</summary>
	/// <param name="Path">"case.user.display_name"; the item values of a list are "items[].name".</param>
	/// <param name="Type">"string", "number", "boolean", "datetime" or "list".</param>
	public sealed record TemplateVariable(string Path, string Type, bool Optional);

	/// <summary>A registered model as an author sees it: what the template can use, and a made-up instance of it.</summary>
	/// <param name="Sample">A JSON object keyed in snake_case with plausible values, which also deserializes into the model.</param>
	public sealed record TemplateModelDescription(string Key, IReadOnlyList<TemplateVariable> Variables, JsonObject Sample);

	public sealed record TemplateLanguageSummary(string Language, int? ActiveVersion, int Versions);

	/// <summary>A template key with the languages that have rows; <see cref="HasModel"/> is false for a key that has rows but no registered model.</summary>
	public sealed record TemplateKeySummary(string Key, bool HasModel, IReadOnlyList<TemplateLanguageSummary> Languages);

	/// <summary>
	/// What an admin screen for notification templates needs: the models and the values they offer, the keys and versions in
	/// the database, new versions (never an edit in place), switching the active version, and trying a draft out.
	///
	/// A version, once written, is not changed: <see cref="CreateVersionAsync"/> always adds a row, inactive, and
	/// <see cref="ActivateAsync"/> is the only thing that moves the "live" flag, so wording can always be switched back.
	/// </summary>
	public sealed class TemplateAuthoring
	{
		public const int MaxLanguageLength = 16;

		private const int MaxDepth = 8;
		private const int CreateAttempts = 3;

		private static readonly Regex LanguagePattern = new("^[A-Za-z]+(-[A-Za-z]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

		/// <summary>Reads and writes the model with the same names the template uses, so a sample and an override are written the way a template reads.</summary>
		private static readonly JsonSerializerOptions ModelJson = createModelJsonOptions();

		private readonly TemplateRegistry mRegistry;
		private readonly TemplateProcessor mProcessor;

		public TemplateAuthoring(TemplateRegistry registry, TemplateProcessor processor)
		{
			mRegistry = registry;
			mProcessor = processor;
		}

		public bool HasModel(string key) => mRegistry.TryGetModelType(key, out _);

		/// <summary>One entry per registered model, sorted by key.</summary>
		public IReadOnlyList<TemplateModelDescription> DescribeModels() =>
			mRegistry.Models.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => Describe(m.Key, m.Value)).ToList();

		public TemplateModelDescription Describe(string key, Type modelType)
		{
			var variables = new List<TemplateVariable>();
			collectVariables(modelType, "", 0, new NullabilityInfoContext(), [modelType], variables);
			return new TemplateModelDescription(key, variables, CreateSample(modelType));
		}

		/// <summary>
		/// A made-up instance of the model as JSON: "Sample &lt;PropertyName&gt;" for text, 1 for numbers, true for flags, the
		/// current time for dates, two items in every list. Keys are snake_case, as templates write them.
		/// </summary>
		public static JsonObject CreateSample(Type modelType) =>
			sampleOf(modelType, "", 0, []) as JsonObject ?? new JsonObject();

		/// <summary>
		/// Makes the model object: the sample of <paramref name="modelType"/>, with <paramref name="overrides"/> laid over it
		/// (objects merged key by key, anything else, lists included, replaced).
		/// </summary>
		/// <exception cref="TemplateModelJsonException">The result does not fit the model class.</exception>
		public static object CreateModel(string key, Type modelType, JsonObject? overrides = null)
		{
			var json = CreateSample(modelType);
			if (overrides != null) merge(json, overrides);

			try
			{
				return json.Deserialize(modelType, ModelJson) ?? throw new TemplateModelJsonException(key, "the model is empty.");
			}
			catch (JsonException ex)
			{
				throw new TemplateModelJsonException(key, ex.Message, ex);
			}
			catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
			{
				throw new TemplateModelJsonException(key, ex.Message, ex);
			}
		}

		/// <summary>
		/// What is wrong with a draft before any template engine sees it, or null. A language is letters with optional dashes
		/// ("en", "pt-br"), at most <see cref="MaxLanguageLength"/> long; the subject is not empty; there is a text or an HTML.
		/// </summary>
		public static string? CheckDraft(string? language, string? subject, string? text, string? html, bool languageRequired = true)
		{
			if (language == null || string.IsNullOrWhiteSpace(language))
			{
				if (languageRequired) return "The language is required.";
			}
			else if (language.Trim().Length > MaxLanguageLength)
			{
				return $"The language must be at most {MaxLanguageLength} characters.";
			}
			else if (!LanguagePattern.IsMatch(language.Trim()))
			{
				return "The language must be a language tag: letters, optionally separated by dashes (en, uk, pt-br).";
			}

			if (string.IsNullOrWhiteSpace(subject)) return "The subject is required.";
			if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(html)) return "The template needs a text or an HTML part.";
			return null;
		}

		/// <summary>The tag as it is stored: trimmed, lower case.</summary>
		public static string NormalizeLanguage(string language) => language.Trim().ToLowerInvariant();

		/// <summary>
		/// Renders a draft against the sample of the key's model and returns the part as a recipient would see it: the values filled
		/// in and the &lt;secure&gt; tags removed with their content kept. <paramref name="modelOverride"/> lays values over the sample.
		/// </summary>
		/// <exception cref="TemplateRenderException">The draft does not parse, reads an unknown value, or leaves &lt;secure&gt; tags unbalanced.</exception>
		/// <exception cref="TemplateModelJsonException">The override does not fit the model.</exception>
		/// <exception cref="TemplateModelException">The override left a non-nullable value null.</exception>
		public async Task<RenderedTemplate> PreviewAsync(string key, string? language, string subject, string? text, string? html, JsonObject? modelOverride = null)
		{
			var modelType = modelTypeOf(key);
			var model = CreateModel(key, modelType, modelOverride);
			var rendered = await mProcessor.RenderDraftAsync(key, subject, text, html, model, language);
			return new RenderedTemplate
			{
				TemplateKey = rendered.TemplateKey,
				Language = rendered.Language,
				Version = rendered.Version,
				Subject = SecureMarkup.Strip(rendered.Subject) ?? "",
				Text = SecureMarkup.Strip(rendered.Text),
				Html = SecureMarkup.Strip(rendered.Html),
			};
		}

		/// <summary>The keys that have rows or a model, with the languages that have rows, all sorted.</summary>
		public async Task<IReadOnlyList<TemplateKeySummary>> ListKeysAsync(IRepository repository, CancellationToken cancellationToken = default)
		{
			var rows = await repository.GetQueryable<NotificationTemplate>()
				.AsNoTracking()
				.Select(t => new { t.TemplateKey, t.Language, t.Version, t.IsActive })
				.ToListAsync(cancellationToken);

			var keys = rows.Select(r => r.TemplateKey).Concat(mRegistry.Models.Select(m => m.Key)).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);
			return keys.Select(key => new TemplateKeySummary(
				key,
				HasModel(key),
				rows.Where(r => r.TemplateKey == key)
					.GroupBy(r => r.Language, StringComparer.Ordinal)
					.OrderBy(g => g.Key, StringComparer.Ordinal)
					.Select(g => new TemplateLanguageSummary(g.Key, g.Where(r => r.IsActive).Select(r => (int?)r.Version).FirstOrDefault(), g.Count()))
					.ToList())).ToList();
		}

		/// <summary>The versions of a key, by language and then newest first. <paramref name="language"/> narrows it to one.</summary>
		public async Task<IReadOnlyList<NotificationTemplate>> ListVersionsAsync(IRepository repository, string key, string? language = null, CancellationToken cancellationToken = default)
		{
			var wanted = string.IsNullOrWhiteSpace(language) ? null : NormalizeLanguage(language);
			return await repository.GetQueryable<NotificationTemplate>(t => t.TemplateKey == key && (wanted == null || t.Language == wanted))
				.AsNoTracking()
				.OrderBy(t => t.Language)
				.ThenByDescending(t => t.Version)
				.ToListAsync(cancellationToken);
		}

		/// <summary>
		/// Adds the next version of the key in the language: inactive, numbered after the highest there is. The draft has to render
		/// against the sample model first. Call <see cref="CheckDraft"/> before, for the input it checks.
		/// </summary>
		/// <exception cref="TemplateRenderException">The draft does not render against the sample.</exception>
		public async Task<NotificationTemplate> CreateVersionAsync(IRepository repository, string key, string language, string subject, string? text, string? html, string? createdBy, CancellationToken cancellationToken = default)
		{
			var modelType = modelTypeOf(key);
			var normalized = NormalizeLanguage(language);
			text = string.IsNullOrWhiteSpace(text) ? null : text;
			html = string.IsNullOrWhiteSpace(html) ? null : html;

			// The same check a preview does, so a version that is saved is one that can be filled in.
			await mProcessor.RenderDraftAsync(key, subject, text, html, CreateModel(key, modelType), normalized);

			for (var attempt = 1; ; attempt++)
			{
				var highest = await repository.GetQueryable<NotificationTemplate>(t => t.TemplateKey == key && t.Language == normalized)
					.MaxAsync(t => (int?)t.Version, cancellationToken);
				var row = new NotificationTemplate
				{
					TemplateKey = key,
					Language = normalized,
					Version = (highest ?? 0) + 1,
					IsActive = false,
					Subject = subject,
					Text = text,
					Html = html,
					CreatedAt = DateTime.UtcNow,
					CreatedBy = createdBy?.Length > 256 ? createdBy[..256] : createdBy,
				};

				repository.Create(row);
				try
				{
					await repository.SaveAsync(cancellationToken);
					return row;
				}
				catch (DbUpdateException) when (attempt < CreateAttempts)
				{
					// Someone else took this number a moment ago (the unique index on key, language and version): take the next.
					repository.Detach(row);
				}
			}
		}

		/// <summary>
		/// Makes the version the live one of its key and language, and the one that was live not. Both changes are one transaction,
		/// and the old one is saved first, because the database allows only one active row per key and language at any moment.
		/// Activating the version that is already active does nothing. The wording is not checked again.
		/// </summary>
		/// <returns>False when there is no such version.</returns>
		public async Task<bool> ActivateAsync(IRepository repository, long id, CancellationToken cancellationToken = default)
		{
			var db = repository.DbContext;
			var found = false;
			await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
			{
				// The strategy may run this block again after a transient failure: start from what the database says.
				db.ChangeTracker.Clear();
				await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

				var row = await repository.GetQueryable<NotificationTemplate>(t => t.Id == id).FirstOrDefaultAsync(cancellationToken);
				found = row != null;
				if (row == null || row.IsActive) return;

				var others = await repository.GetQueryable<NotificationTemplate>(t => t.TemplateKey == row.TemplateKey && t.Language == row.Language && t.IsActive && t.Id != id)
					.ToListAsync(cancellationToken);
				foreach (var other in others) other.IsActive = false;
				if (others.Count > 0) await repository.SaveAsync(cancellationToken);

				row.IsActive = true;
				await repository.SaveAsync(cancellationToken);
				await transaction.CommitAsync(cancellationToken);
			});

			return found;
		}

		private Type modelTypeOf(string key) =>
			mRegistry.TryGetModelType(key, out var type) ? type : throw new InvalidOperationException($"No template model is registered for key '{key}'.");

		// ---- variables ----

		private static void collectVariables(Type type, string prefix, int depth, NullabilityInfoContext context, List<Type> stack, List<TemplateVariable> variables)
		{
			if (depth > MaxDepth) return;

			foreach (var property in TemplateVariableChecker.PublicProperties(type).Where(p => p.CanRead))
			{
				var path = prefix + MemberNameStrategies.SnakeCase(property);
				var optional = TemplateModelValidator.IsOptional(property, context);
				var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

				switch (kindOf(propertyType))
				{
					case Kind.Dictionary:
						// Keys are not known before the data is.
						break;
					case Kind.List:
						variables.Add(new TemplateVariable(path, "list", optional));
						var element = TemplateVariableChecker.ElementTypeOf(propertyType);
						if (element != null && kindOf(Nullable.GetUnderlyingType(element) ?? element) == Kind.Object && !stack.Contains(element))
						{
							stack.Add(element);
							collectVariables(element, path + "[].", depth + 1, context, stack, variables);
							stack.RemoveAt(stack.Count - 1);
						}

						break;
					case Kind.Object:
						if (stack.Contains(propertyType)) break;
						stack.Add(propertyType);
						collectVariables(propertyType, path + ".", depth + 1, context, stack, variables);
						stack.RemoveAt(stack.Count - 1);
						break;
					default:
						variables.Add(new TemplateVariable(path, typeName(kindOf(propertyType)), optional));
						break;
				}
			}
		}

		private enum Kind { Text, Number, Boolean, DateTime, List, Dictionary, Object }

		private static Kind kindOf(Type type)
		{
			if (type == typeof(string) || type == typeof(char) || type.IsEnum) return Kind.Text;
			if (type == typeof(bool)) return Kind.Boolean;
			if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly)) return Kind.DateTime;
			if (type.IsPrimitive || type == typeof(decimal)) return Kind.Number;
			if (typeof(IDictionary).IsAssignableFrom(type) || type.GetInterfaces().Append(type).Any(i => i.IsGenericType && i.GetGenericTypeDefinition() is var d && (d == typeof(IDictionary<,>) || d == typeof(IReadOnlyDictionary<,>)))) return Kind.Dictionary;
			if (typeof(IEnumerable).IsAssignableFrom(type)) return Kind.List;
			return TemplateModelValidator.IsComplex(type) ? Kind.Object : Kind.Text;
		}

		private static string typeName(Kind kind) => kind switch
		{
			Kind.Number => "number",
			Kind.Boolean => "boolean",
			Kind.DateTime => "datetime",
			Kind.List => "list",
			_ => "string",
		};

		// ---- samples ----

		private static JsonNode? sampleOf(Type type, string propertyName, int depth, List<Type> stack)
		{
			type = Nullable.GetUnderlyingType(type) ?? type;
			switch (kindOf(type))
			{
				case Kind.Boolean:
					return JsonValue.Create(true);
				case Kind.Number:
					return JsonValue.Create(1);
				case Kind.DateTime:
					var now = DateTime.UtcNow;
					return JsonValue.Create(type == typeof(DateOnly)
						? now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
						: now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
				case Kind.Dictionary:
					return new JsonObject();
				case Kind.List:
					var element = TemplateVariableChecker.ElementTypeOf(type);
					var items = new JsonArray();
					if (element != null && depth <= MaxDepth)
					{
						for (var i = 0; i < 2; i++)
						{
							var item = sampleOf(element, propertyName, depth + 1, stack);
							if (item != null) items.Add(item);
						}
					}

					return items;
				case Kind.Object:
					if (depth > MaxDepth || stack.Contains(type)) return null;
					stack.Add(type);
					var result = new JsonObject();
					foreach (var property in TemplateVariableChecker.PublicProperties(type).Where(p => p.CanRead))
					{
						var value = sampleOf(property.PropertyType, property.Name, depth + 1, stack);
						if (value != null) result[MemberNameStrategies.SnakeCase(property)] = value;
					}

					stack.RemoveAt(stack.Count - 1);
					return result;
				default:
					if (type.IsEnum) return JsonValue.Create(Enum.GetNames(type).FirstOrDefault() ?? "");
					if (type == typeof(char)) return JsonValue.Create("A");
					if (type == typeof(string)) return JsonValue.Create($"Sample {propertyName}");
					if (type == typeof(Guid)) return JsonValue.Create("00000000-0000-0000-0000-000000000001");
					if (type == typeof(Uri)) return JsonValue.Create("https://example.com/sample");
					if (type == typeof(TimeSpan)) return JsonValue.Create("00:01:00");
					return JsonValue.Create($"Sample {propertyName}");
			}
		}

		private static void merge(JsonObject target, JsonObject overrides)
		{
			foreach (var (name, value) in overrides)
			{
				if (value is JsonObject over && target[name] is JsonObject existing) merge(existing, over);
				else target[name] = value?.DeepClone();
			}
		}

		private static JsonSerializerOptions createModelJsonOptions()
		{
			var options = new JsonSerializerOptions
			{
				PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
				TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { snakeCaseNames } },
			};
			options.Converters.Add(new JsonStringEnumConverter());
			return options;
		}

		/// <summary>Names every property the way a template does (the engine's own snake_case rule), so "ExpiresAtUtc" is "expires_at_utc" here and there.</summary>
		private static void snakeCaseNames(JsonTypeInfo typeInfo)
		{
			if (typeInfo.Kind != JsonTypeInfoKind.Object) return;
			foreach (var property in typeInfo.Properties)
			{
				if (property.AttributeProvider is MemberInfo member) property.Name = MemberNameStrategies.SnakeCase(member);
			}
		}
	}
}
