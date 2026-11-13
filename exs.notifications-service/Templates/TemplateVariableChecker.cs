using System.Collections;
using System.Reflection;
using Fluid;
using Fluid.Ast;
using Fluid.Parser;

namespace exs.notifications_service.Templates
{
	/// <summary>
	/// Reads a parsed template and lists the values it asks for that its model does not have: {{ case.user.display_nmae }}.
	/// The engine itself cannot tell: below the first name it renders a name that does not exist as an empty string, which
	/// is what a typo would turn into on the day a customer is emailed.
	///
	/// It follows the plain property names of a path through the model's own classes, and stops where it can no longer be
	/// sure: an index ({{ items[0].name }}), a dictionary, a list (a loop variable is a different object), or a name that is
	/// not a property of the model at all, since that is a variable the template made itself ({% assign %}, {% for %}).
	/// A first name that is neither is caught at render time instead, when the engine finds it undefined.
	/// </summary>
	internal static class TemplateVariableChecker
	{
		/// <summary>The paths, as written in the template, that the model does not have.</summary>
		public static IReadOnlyList<string> FindUnknown(IFluidTemplate template, Type modelType)
		{
			if (template is not FluidTemplate fluidTemplate) return [];

			var collector = new Collector();
			foreach (var statement in fluidTemplate.Statements) collector.Visit(statement);

			var unknown = new List<string>();
			foreach (var path in collector.Paths)
			{
				if (collector.Locals.Contains(path[0])) continue;

				var problem = walk(path, modelType);
				if (problem != null) unknown.Add(problem);
			}

			return unknown.Distinct().ToList();
		}

		private static string? walk(IReadOnlyList<string> path, Type modelType)
		{
			var type = modelType;
			for (var i = 0; i < path.Count; i++)
			{
				if (!TemplateModelValidator.IsComplex(type) || typeof(IEnumerable).IsAssignableFrom(type)) return null;

				var property = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
					.FirstOrDefault(p => p.GetIndexParameters().Length == 0 && string.Equals(MemberNameStrategies.SnakeCase(p), path[i], StringComparison.OrdinalIgnoreCase));
				if (property == null)
				{
					// The first name may be a variable the template made itself; deeper down it can only be a typo.
					return i == 0 ? null : $"{string.Join(".", path.Take(i + 1))} ({Describe(type)} has no '{path[i]}')";
				}

				type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
			}

			return null;
		}

		/// <summary>"CaseInfo" and what it offers, so the error says what the author could have meant.</summary>
		private static string Describe(Type type) =>
			$"{type.Name} (has: {string.Join(", ", type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).Select(p => MemberNameStrategies.SnakeCase(p)))})";

		private sealed class Collector : AstVisitor
		{
			public List<IReadOnlyList<string>> Paths { get; } = [];

			/// <summary>Names the template defines for itself, in any scope. Treating them as defined everywhere is the lenient side: it can only skip a check, never invent a problem.</summary>
			public HashSet<string> Locals { get; } = new(StringComparer.OrdinalIgnoreCase);

			protected override Expression VisitMemberExpression(MemberExpression memberExpression)
			{
				// Only the leading run of plain names: an indexer or a call ends what can be followed.
				var names = new List<string>();
				foreach (var segment in memberExpression.Segments)
				{
					if (segment is IdentifierSegment identifier) names.Add(identifier.Identifier);
					else break;
				}

				if (names.Count > 0) Paths.Add(names);
				return base.VisitMemberExpression(memberExpression);
			}

			protected override Statement VisitForStatement(ForStatement forStatement)
			{
				Locals.Add(forStatement.Identifier);
				return base.VisitForStatement(forStatement);
			}

			protected override Statement VisitAssignStatement(AssignStatement assignStatement)
			{
				Locals.Add(assignStatement.Identifier);
				return base.VisitAssignStatement(assignStatement);
			}

			protected override Statement VisitCaptureStatement(CaptureStatement captureStatement)
			{
				Locals.Add(captureStatement.Identifier);
				return base.VisitCaptureStatement(captureStatement);
			}
		}
	}
}
