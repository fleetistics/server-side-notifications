using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using exs.Database.Commons.Interfaces;
using exs.notifications_contracts;
using exs.notifications_model.Templates;
using Fluid;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace exs.notifications_service.Templates
{
	/// <summary>
	/// Turns a model into the subject, text and HTML of a message: finds the active template for the model's key in the
	/// wanted language (or the default one), checks that the model has what it declares, and fills the template in.
	///
	/// What it guarantees, in the order a problem would show:
	///  - no row for the key: <see cref="TemplateNotFoundException"/>, so a missing template is a loud failure of the
	///    publishing call instead of an empty email;
	///  - a non-nullable property that is null: <see cref="TemplateModelException"/>;
	///  - a template that does not parse, or reads a name the model does not have: <see cref="TemplateRenderException"/>
	///    (a name the model does not have is an error, never an empty string);
	///  - the result has well-formed `&lt;secure&gt;` tags (<see cref="SecureMarkup"/>), and no value the model supplied can form
	///    a tag, so what the sender masks is exactly what the template author marked.
	///
	/// Templates are plain text with {{ values }} in snake_case ({{ case.user.display_name }}), and nothing a model does not
	/// expose can be reached. Values are HTML-escaped in the Html part and used as they are in Subject and Text.
	/// </summary>
	public sealed class TemplateProcessor
	{
		/// <summary>Escapes only what HTML needs escaped. The default encoder also turns every non-Latin letter into a numeric entity, which mangles a Ukrainian name in the source of the email.</summary>
		private static readonly HtmlEncoder HtmlEscaper = HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All);

		private readonly TemplateRegistry mRegistry;
		private readonly IOptionsMonitor<NotificationTemplateOptions> mOptions;
		private readonly FluidParser mParser = new();
		private readonly Fluid.TemplateOptions mFluidOptions = new();

		public TemplateProcessor(TemplateRegistry registry, IOptionsMonitor<NotificationTemplateOptions> options)
		{
			mRegistry = registry;
			mOptions = options;

			mFluidOptions.MemberAccessStrategy.MemberNameStrategy = MemberNameStrategies.SnakeCase;
			// Not StrictVariables: the engine counts a null property as undefined, which would forbid the optional values
			// (string? Nickname) that a template tests with {% if %}. A name that does not exist at all is refused here instead,
			// and a mistyped name below the first one is found by TemplateVariableChecker before rendering.
			mFluidOptions.Undefined = name => throw new InvalidOperationException($"'{name}' is not a value of the model.");
			mFluidOptions.StrictFilters = true;

			// Every text a model supplies passes through here before the template can place it between its own tags.
			// (A literal in the template does not: it is the template author's.)
			mFluidOptions.ValueConverters.Add(value => value is string text ? SecureMarkup.Neutralize(text) : null);

			var registered = new HashSet<Type>();
			foreach (var model in registry.Models) exposeGraph(model.Value, registered);
		}

		/// <summary>Renders the active template of <typeparamref name="TModel"/>.</summary>
		/// <param name="language">Wanted language ("uk", "pt-BR"), or null for the default.</param>
		public async Task<RenderedTemplate> RenderAsync<TModel>(IRepository repository, TModel model, string? language = null, CancellationToken cancellationToken = default)
			where TModel : class, ITemplateModel
		{
			if (repository == null) throw new ArgumentNullException(nameof(repository));
			if (model == null) throw new ArgumentNullException(nameof(model));

			var key = TModel.TemplateKey;
			if (!mRegistry.TryGetModelType(key, out var registered) || registered != typeof(TModel))
			{
				throw new InvalidOperationException($"{typeof(TModel).FullName} is not a registered template model. Pass its assembly to AddNotificationTemplates.");
			}

			var missing = TemplateModelValidator.FindMissing(model);
			if (missing.Count > 0) throw new TemplateModelException(key, missing);

			var candidates = languageCandidates(language, mOptions.CurrentValue.DefaultLanguage);
			var rows = await repository.GetQueryable<NotificationTemplate>()
				.Where(t => t.TemplateKey == key && t.IsActive && candidates.Contains(t.Language))
				.ToListAsync(cancellationToken);
			var template = candidates.Select(c => rows.FirstOrDefault(r => r.Language == c)).FirstOrDefault(r => r != null)
				?? throw new TemplateNotFoundException(key, candidates);

			var culture = cultureOf(template.Language);
			return new RenderedTemplate
			{
				TemplateKey = key,
				Language = template.Language,
				Version = template.Version,
				Subject = (await renderPart(template, "Subject", template.Subject, model, culture, plainText: true))!,
				Text = await renderPart(template, "Text", template.Text, model, culture, plainText: true),
				Html = await renderPart(template, "Html", template.Html, model, culture, plainText: false),
			};
		}

		private async Task<string?> renderPart(NotificationTemplate template, string part, string? source, object model, CultureInfo culture, bool plainText)
		{
			if (string.IsNullOrWhiteSpace(source)) return null;

			if (!mParser.TryParse(source, out var parsed, out var error))
			{
				throw new TemplateRenderException(template.TemplateKey, template.Language, template.Version, part, error);
			}

			var unknown = TemplateVariableChecker.FindUnknown(parsed, model.GetType());
			if (unknown.Count > 0)
			{
				throw new TemplateRenderException(template.TemplateKey, template.Language, template.Version, part, $"it reads values the model does not have: {string.Join("; ", unknown)}.");
			}

			string rendered;
			try
			{
				var context = new TemplateContext(model, mFluidOptions) { CultureInfo = culture };
				rendered = await parsed.RenderAsync(context, plainText ? NullEncoder.Default : HtmlEscaper);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// The engine's text names the variable or filter, not a value.
				throw new TemplateRenderException(template.TemplateKey, template.Language, template.Version, part, ex.Message, ex);
			}

			var tagProblem = SecureMarkup.Validate(rendered);
			if (tagProblem != null) throw new TemplateRenderException(template.TemplateKey, template.Language, template.Version, part, $"the <secure> tags are wrong: {tagProblem}");

			return rendered;
		}

		/// <summary>"uk-UA" asks for uk-ua, then uk, then the default.</summary>
		private static IReadOnlyList<string> languageCandidates(string? wanted, string defaultLanguage)
		{
			var candidates = new List<string>();
			var normalized = wanted?.Trim().Replace('_', '-').ToLowerInvariant();
			if (!string.IsNullOrEmpty(normalized))
			{
				candidates.Add(normalized);
				var dash = normalized.IndexOf('-');
				if (dash > 0) candidates.Add(normalized[..dash]);
			}

			candidates.Add(defaultLanguage.Trim().ToLowerInvariant());
			return candidates.Distinct().ToList();
		}

		private static CultureInfo cultureOf(string language)
		{
			try
			{
				return CultureInfo.GetCultureInfo(language);
			}
			catch (CultureNotFoundException)
			{
				return CultureInfo.InvariantCulture;
			}
		}

		/// <summary>
		/// Lets the template engine read the model and the classes it holds, and nothing else. The engine refuses any type that
		/// has not been registered, so what is registered here is the whole of what a template can reach.
		/// </summary>
		private void exposeGraph(Type type, HashSet<Type> registered)
		{
			type = Nullable.GetUnderlyingType(type) ?? type;
			if (!TemplateModelValidator.IsComplex(type) || !registered.Add(type)) return;

			mFluidOptions.MemberAccessStrategy.Register(type);
			foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
			{
				if (property.GetIndexParameters().Length > 0) continue;
				foreach (var held in heldTypes(property.PropertyType)) exposeGraph(held, registered);
			}
		}

		/// <summary>The type itself, or what a list or dictionary of it holds.</summary>
		private static IEnumerable<Type> heldTypes(Type type)
		{
			if (type.IsArray) return [type.GetElementType()!];
			if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type) && type.IsGenericType) return type.GetGenericArguments();
			return [type];
		}
	}
}
