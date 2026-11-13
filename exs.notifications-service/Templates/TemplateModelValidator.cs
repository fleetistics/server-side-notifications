using System.Collections;
using System.Reflection;

namespace exs.notifications_service.Templates
{
	/// <summary>
	/// "Are all the parameters there": finds the properties that the model's own declaration says cannot be null (a
	/// non-nullable reference type, as the compiler sees it) but are, anywhere in the object it holds, and the lists it
	/// holds. A property declared nullable (string?) is allowed to be missing, and the template then deals with it
	/// ({% if %}). Reports property paths only, never values.
	/// </summary>
	internal static class TemplateModelValidator
	{
		private const int MaxDepth = 8;

		public static IReadOnlyList<string> FindMissing(object model)
		{
			var missing = new List<string>();
			walk(model, "", 0, new NullabilityInfoContext(), new HashSet<object>(ReferenceEqualityComparer.Instance), missing);
			return missing;
		}

		/// <summary>A type whose properties are worth walking: ours and the host's, not the framework's strings, numbers, dates and the like.</summary>
		internal static bool IsComplex(Type type)
		{
			type = Nullable.GetUnderlyingType(type) ?? type;
			if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(object)) return false;
			if (type.Namespace != null && type.Namespace.StartsWith("System", StringComparison.Ordinal)) return false;
			return type.IsClass || type.IsValueType;
		}

		private static void walk(object value, string path, int depth, NullabilityInfoContext context, HashSet<object> seen, List<string> missing)
		{
			if (depth > MaxDepth || !IsComplex(value.GetType())) return;
			if (!value.GetType().IsValueType && !seen.Add(value)) return;

			foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
			{
				if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;

				var propertyPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
				var propertyValue = property.GetValue(value);
				if (propertyValue == null)
				{
					if (!property.PropertyType.IsValueType && context.Create(property).ReadState == NullabilityState.NotNull) missing.Add(propertyPath);
					continue;
				}

				if (propertyValue is IEnumerable items && propertyValue is not string)
				{
					var index = 0;
					foreach (var item in items)
					{
						if (item != null) walk(item, $"{propertyPath}[{index}]", depth + 1, context, seen, missing);
						index++;
					}
				}
				else
				{
					walk(propertyValue, propertyPath, depth + 1, context, seen, missing);
				}
			}
		}
	}
}
