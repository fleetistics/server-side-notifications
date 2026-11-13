using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace exs.notifications_service.Templates
{
	public static class TemplateDiHelper
	{
		/// <summary>
		/// Registers <see cref="TemplateProcessor"/> and the <see cref="TemplateRegistry"/> of every <see cref="ITemplateModel"/>
		/// found in the given assemblies (the host's own, where its models are declared). Throws at once if two models share a key.
		/// </summary>
		/// <param name="configuration">Optional: binds "NotificationService:Templates" to <see cref="NotificationTemplateOptions"/>.</param>
		public static IServiceCollection AddNotificationTemplates(this IServiceCollection services, IEnumerable<Assembly> modelAssemblies, IConfiguration? configuration = null)
		{
			var options = services.AddOptions<NotificationTemplateOptions>();
			if (configuration != null) options.Bind(configuration.GetSection(NotificationTemplateOptions.SectionName));

			services.AddSingleton(new TemplateRegistry(modelAssemblies));
			services.AddSingleton<TemplateProcessor>();
			return services;
		}
	}
}
