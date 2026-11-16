using System.Reflection;
using Fluid;

namespace exs.notifications_service.Templates
{
	/// <summary>
	/// The one rule that names a model property for a template: exactly as the C# property is declared, PascalCase
	/// ("ExpiresAtUtc", "Case.User.DisplayName"), and matched case-sensitively, so "userName" is not "UserName". The engine,
	/// the list of variables, the sample, the unknown-name check and the JSON of a model all use it, so they cannot
	/// disagree about a name, and a preview behaves like the real send.
	/// </summary>
	internal static class TemplateNaming
	{
		/// <summary>The name a template uses for the property or field.</summary>
		public static string NameOf(MemberInfo member) => MemberNameStrategies.Default(member);

		/// <summary>The same rule as the delegate the engine takes.</summary>
		public static MemberNameStrategy Strategy { get; } = NameOf;
	}
}
