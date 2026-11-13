using System.Reflection;

namespace exs.notifications_service.Templates
{
	/// <summary>
	/// Which model class belongs to which template key. Built once, at startup, from the assemblies the host names, by
	/// looking for every class that implements <see cref="ITemplateModel"/>; two classes with one key is a startup error,
	/// not a surprise at the first send.
	/// </summary>
	public sealed class TemplateRegistry
	{
		private readonly Dictionary<string, Type> mByKey = new(StringComparer.Ordinal);
		private readonly Dictionary<Type, string> mByType = new();

		public TemplateRegistry(IEnumerable<Assembly> assemblies)
			: this(assemblies.SelectMany(a => a.GetTypes()))
		{
		}

		public TemplateRegistry(IEnumerable<Type> candidateTypes)
		{
			foreach (var type in candidateTypes)
			{
				if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition || !typeof(ITemplateModel).IsAssignableFrom(type)) continue;

				var key = readKey(type);
				if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException($"{type.FullName}.{nameof(ITemplateModel.TemplateKey)} is empty.");
				if (mByKey.TryGetValue(key, out var other)) throw new InvalidOperationException($"Template key '{key}' is declared by both {other.FullName} and {type.FullName}.");

				mByKey[key] = type;
				mByType[type] = key;
			}
		}

		/// <summary>Every model type found, with its key.</summary>
		public IReadOnlyCollection<KeyValuePair<string, Type>> Models => mByKey;

		public bool TryGetModelType(string key, out Type modelType) => mByKey.TryGetValue(key, out modelType!);

		/// <summary>The key of a model type. Throws when the type was not found by the scan, which means its assembly was not given to it.</summary>
		public string KeyOf(Type modelType) =>
			mByType.TryGetValue(modelType, out var key)
				? key
				: throw new InvalidOperationException($"{modelType.FullName} is not a registered template model. Pass its assembly to AddNotificationTemplates.");

		// A static abstract member has no instance to read it from, and the type is only known at run time, so it goes through a generic method.
		private static string readKey(Type type) =>
			(string)typeof(TemplateRegistry).GetMethod(nameof(keyOf), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(type).Invoke(null, null)!;

		private static string keyOf<T>() where T : ITemplateModel => T.TemplateKey;
	}
}
