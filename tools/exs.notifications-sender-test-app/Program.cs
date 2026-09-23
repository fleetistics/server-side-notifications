using System.Reflection;
using exs.Database.Commons.Interfaces;
using exs.notifications_model.Notifications;
using exs.notifications_service.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FcmDiHelper = exs.fcm_sender.core.DiHelper;
using NotificationDiHelper = exs.notifications_service.core.DiHelper;

var builder = Host.CreateApplicationBuilder(args);
LoadConfiguration(builder.Configuration);

FcmDiHelper.RegisterServices(builder.Services, builder.Configuration);
NotificationDiHelper.RegisterServices(builder.Services, builder.Configuration);

var host = builder.Build();
await host.StartAsync();

try
{
	using (var scope = host.Services.CreateScope())
	{
		var sender = scope.ServiceProvider.GetRequiredService<INotificationSender>();
		var repository = scope.ServiceProvider.GetRequiredService<IRepository>();

		sender.CreateNotification(
			repository,
			notificationType: NotificationType.Generic,
			entityId: 95,
			title: "Test notification",
			body: "Created from exs.notifications-sender-test-app for user 95.",
			payload: "{\"source\":\"exs.notifications-sender-test-app\"}",
			userIds: [95]);

		repository.Save();
	}

	await Task.Delay(TimeSpan.FromSeconds(15));
}
finally
{
	await host.StopAsync();
}

static void LoadConfiguration(ConfigurationManager configuration)
{
	foreach (var candidate in GetConfigCandidates())
	{
		if (File.Exists(candidate))
		{
			Console.WriteLine($"Loading configuration: {candidate}");
			configuration.AddJsonFile(candidate, optional: false, reloadOnChange: false);
		}
	}
}

static IEnumerable<string> GetConfigCandidates()
{
	var baseDirectories = new[]
	{
		Directory.GetCurrentDirectory(),
		AppContext.BaseDirectory,
		Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? string.Empty,
	};

	var relativePaths = new[]
	{
		Path.Combine("local_configs", "local_server.json"),
		Path.Combine("..", "local_configs", "local_server.json"),
		Path.Combine("..", "..", "local_configs", "local_server.json"),
		Path.Combine("exs.fcm-sender", "local_configs", "local_server.json"),
		Path.Combine("..", "exs.fcm-sender", "local_configs", "local_server.json"),
		Path.Combine("..", "..", "exs.fcm-sender", "local_configs", "local_server.json"),
		Path.Combine("..", "..", "..", "exs.fcm-sender", "local_configs", "local_server.json"),
	};

	foreach (var baseDirectory in baseDirectories)
	{
		foreach (var relativePath in relativePaths)
		{
			yield return Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
		}
	}
}
